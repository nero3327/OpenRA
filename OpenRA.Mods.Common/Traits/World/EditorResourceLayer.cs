#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	using CellContents = ResourceLayer.CellContents;

	[Desc("Required for the map editor to work. Attach this to the world actor.")]
	public class EditorResourceLayerInfo : TraitInfo, Requires<ResourceTypeInfo>
	{
		public override object Create(ActorInitializer init) { return new EditorResourceLayer(init.Self); }
	}

	public class EditorResourceLayer : IWorldLoaded, IRenderOverlay, INotifyActorDisposing
	{
		protected readonly Map Map;
		protected readonly TileSet Tileset;
		protected readonly Dictionary<int, ResourceType> Resources;
		protected readonly CellLayer<EditorCellContents> Tiles;
		protected readonly HashSet<CPos> Dirty = new HashSet<CPos>();

		readonly Dictionary<PaletteReference, TerrainSpriteLayer> spriteLayers = new Dictionary<PaletteReference, TerrainSpriteLayer>();

		public int NetWorth { get; protected set; }

		bool disposed;

		public EditorResourceLayer(Actor self)
		{
			if (self.World.Type != WorldType.Editor)
				return;

			Map = self.World.Map;
			Tileset = self.World.Map.Rules.TileSet;

			Tiles = new CellLayer<EditorCellContents>(Map);
			Resources = self.TraitsImplementing<ResourceType>()
				.ToDictionary(r => r.Info.ResourceType, r => r);

			Map.Resources.CellEntryChanged += UpdateCell;
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			if (w.Type != WorldType.Editor)
				return;

			foreach (var cell in Map.AllCells)
				UpdateCell(cell);

			// Build the sprite layer dictionary for rendering resources
			// All resources that have the same palette must also share a sheet and blend mode
			foreach (var r in Resources)
			{
				var res = r;
				var layer = spriteLayers.GetOrAdd(r.Value.Palette, pal =>
				{
					var first = res.Value.Variants.First().Value.GetSprite(0);
					return new TerrainSpriteLayer(w, wr, first.Sheet, first.BlendMode, pal, false);
				});

				// Validate that sprites are compatible with this layer
				var sheet = layer.Sheet;
				var sprites = res.Value.Variants.Values.SelectMany(v => Exts.MakeArray(v.Length, x => v.GetSprite(x)));
				if (sprites.Any(s => s.Sheet != sheet))
					throw new InvalidDataException("Resource sprites span multiple sheets. Try loading their sequences earlier.");

				var blendMode = layer.BlendMode;
				if (sprites.Any(s => s.BlendMode != blendMode))
					throw new InvalidDataException("Resource sprites specify different blend modes. "
						+ "Try using different palettes for resource types that use different blend modes.");
			}
		}

		public void UpdateCell(CPos cell)
		{
			var uv = cell.ToMPos(Map);
			var tile = Map.Resources[uv];

			var t = Tiles[cell];
			if (t.Density > 0)
				NetWorth -= (t.Density + 1) * t.Type.Info.ValuePerUnit;

			ResourceType type;
			if (Resources.TryGetValue(tile.Type, out type))
			{
				Tiles[uv] = new EditorCellContents
				{
					Type = type,
					Variant = ChooseRandomVariant(type),
				};

				Map.CustomTerrain[uv] = Tileset.GetTerrainIndex(type.Info.TerrainType);
			}
			else
			{
				Tiles[uv] = EditorCellContents.Empty;
				Map.CustomTerrain[uv] = byte.MaxValue;
			}

			// Ingame resource rendering is a giant hack (#6395),
			// so we must also touch all the neighbouring tiles
			Dirty.Add(cell);
			foreach (var d in CVec.Directions)
				Dirty.Add(cell + d);
		}

		protected virtual string ChooseRandomVariant(ResourceType t)
		{
			return t.Variants.Keys.Random(Game.CosmeticRandom);
		}

		public int ResourceDensityAt(CPos c)
		{
			// Set density based on the number of neighboring resources
			var adjacent = 0;
			var type = Tiles[c].Type;
			var resources = Map.Resources;
			for (var u = -1; u < 2; u++)
			{
				for (var v = -1; v < 2; v++)
				{
					var cell = c + new CVec(u, v);
					if (resources.Contains(cell) && resources[cell].Type == type.Info.ResourceType)
						adjacent++;
				}
			}

			return Math.Max(int2.Lerp(0, type.Info.MaxDensity, adjacent, 9), 1);
		}

		public virtual EditorCellContents UpdateDirtyTile(CPos c)
		{
			var t = Tiles[c];
			var type = t.Type;

			// Empty tile
			if (type == null)
			{
				t.Sequence = null;
				return t;
			}

			// Density + 1 as workaround for fixing ResourceLayer.Harvest as it would be very disruptive to balancing
			if (t.Density > 0)
				NetWorth -= (t.Density + 1) * type.Info.ValuePerUnit;

			// Set density based on the number of neighboring resources
			t.Density = ResourceDensityAt(c);

			NetWorth += (t.Density + 1) * type.Info.ValuePerUnit;

			t.Sequence = type.Variants[t.Variant];
			t.Frame = int2.Lerp(0, t.Sequence.Length - 1, t.Density, type.Info.MaxDensity);

			return t;
		}

		void IRenderOverlay.Render(WorldRenderer wr)
		{
			if (wr.World.Type != WorldType.Editor)
				return;

			foreach (var c in Dirty)
			{
				if (Tiles.Contains(c))
				{
					var resource = UpdateDirtyTile(c);
					Tiles[c] = resource;

					foreach (var kv in spriteLayers)
					{
						// resource.Type is meaningless (and may be null) if resource.Sequence is null
						if (resource.Sequence != null && resource.Type.Palette == kv.Key)
							kv.Value.Update(c, resource.Sequence, resource.Frame);
						else
							kv.Value.Clear(c);
					}
				}
			}

			Dirty.Clear();

			foreach (var l in spriteLayers.Values)
				l.Draw(wr.Viewport);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			foreach (var kv in spriteLayers.Values)
				kv.Dispose();

			Map.Resources.CellEntryChanged -= UpdateCell;

			disposed = true;
		}
	}

	public struct EditorCellContents
	{
		public static readonly EditorCellContents Empty = default(EditorCellContents);
		public ResourceType Type;
		public int Density;
		public string Variant;
		public ISpriteSequence Sequence;
		public int Frame;
	}
}
