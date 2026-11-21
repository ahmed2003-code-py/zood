namespace Zawed.Domain;

/// <summary>
/// Fuels sold at Egyptian stations (Java <c>platform.FuelType</c>). The database checks the same list (V4), and the
/// names are the wire and database names. Declared here, not in the API's platform, because the fueling rules of
/// <c>Zawed.Domain</c> need it and the domain references nothing else.
/// </summary>
public enum FuelType
{
    GAS_80,
    GAS_92,
    GAS_95,
    DIESEL,
    CNG,
}
