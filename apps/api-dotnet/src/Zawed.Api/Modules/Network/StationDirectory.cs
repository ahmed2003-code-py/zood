using Zawed.Api.Modules.Network.Contracts;
using Zawed.Api.Platform.Persistence;

namespace Zawed.Api.Modules.Network;

/// <summary><see cref="IStationDirectory"/> on the caller's transaction (Java <c>NetworkStationDirectory</c>).</summary>
public sealed class StationDirectory(IDbSession session) : IStationDirectory
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid Operator_Id { get; set; }
        public string Name_Ar { get; set; } = "";
        public string Name_En { get; set; } = "";
        public string Time_Zone { get; set; } = "";
        public string[] Fuel_Types { get; set; } = [];
        public string Status { get; set; } = "";
        public string Operator_Status { get; set; } = "";
    }

    public async Task<StationRef?> FindStationAsync(Guid stationId, CancellationToken ct = default)
    {
        // Open for fuelings only while both the station and its operator are active: suspending an operator stops its stations.
        var row = await session.Current.QuerySingleOrDefaultAsync<Row>("""
            select s.id, s.operator_id, s.name_ar, s.name_en, s.time_zone, s.fuel_types, s.status, o.status as operator_status
            from stations s join station_operators o on o.id = s.operator_id
            where s.id = @id
            """, new { id = stationId }, ct);
        return row is null ? null : new StationRef(row.Id, row.Operator_Id, row.Name_Ar, row.Name_En, row.Time_Zone, Zones.Info(row.Time_Zone),
            row.Fuel_Types.Select(Enum.Parse<Zawed.Domain.FuelType>).Distinct().Order().ToList(),
            row.Status == "ACTIVE" && row.Operator_Status == "ACTIVE");
    }
}
