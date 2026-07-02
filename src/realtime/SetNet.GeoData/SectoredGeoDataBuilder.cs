using System;
using System.Collections.Generic;

namespace SetNet.GeoData
{
    /// <summary>Builds a <see cref="SectoredGeoData"/> from per-sector geodatas. Add each sector's geodata (its bounds
    /// default to the geodata's own <see cref="IGeoData.Bounds"/>, or pass an explicit box if sectors overlap/tile
    /// tightly), then <see cref="Build"/>.</summary>
    public sealed class SectoredGeoDataBuilder
    {
        private readonly List<SectoredGeoData.Sector> _sectors = new List<SectoredGeoData.Sector>();
        private float _walkStepTolerance = 1f;

        /// <summary>Adds a sector, using the geodata's own bounds as its footprint.</summary>
        public SectoredGeoDataBuilder Add(string id, IGeoData geo)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            _sectors.Add(new SectoredGeoData.Sector(id, geo, geo.Bounds));
            return this;
        }

        /// <summary>Adds a sector with an explicit world-space footprint (use when tiling sectors edge-to-edge).</summary>
        public SectoredGeoDataBuilder Add(string id, IGeoData geo, Bounds bounds)
        {
            if (geo == null) throw new ArgumentNullException(nameof(geo));
            _sectors.Add(new SectoredGeoData.Sector(id, geo, bounds));
            return this;
        }

        /// <summary>Sets the height-step tolerance for can-walk-straight checks that cross a sector border (default 1).</summary>
        public SectoredGeoDataBuilder SetWalkStepTolerance(float tolerance) { _walkStepTolerance = tolerance; return this; }

        /// <summary>Builds the immutable sectored geodata.</summary>
        public SectoredGeoData Build() => new SectoredGeoData(_sectors.ToArray(), _walkStepTolerance);
    }
}
