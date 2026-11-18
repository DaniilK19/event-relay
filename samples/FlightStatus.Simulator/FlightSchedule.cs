namespace FlightStatus.Simulator;

// Everything here is fictional: airlines, airports, flight numbers and times are made up
// for the demo and do not come from any real schedule or flight-data provider.

public sealed record Airline(string Code, string Name);

public sealed record Flight(Airline Airline, int Number, string Origin, string Destination, TimeOnly Departure, string Terminal, string Gate)
{
    public string FlightNumber => $"{Airline.Code}{Number}";
}

public static class FlightSchedule
{
    private static readonly Airline ContosoAir = new("CT", "Contoso Air");
    private static readonly Airline FabrikamAirways = new("FB", "Fabrikam Airways");
    private static readonly Airline TailspinAirlines = new("TS", "Tailspin Airlines");

    public static IReadOnlyList<Flight> Flights { get; } =
    [
        new(ContosoAir, 101, "NTH", "STH", new TimeOnly(6, 45), "1", "A3"),
        new(ContosoAir, 214, "NTH", "EVL", new TimeOnly(9, 10), "1", "A7"),
        new(ContosoAir, 388, "LKS", "NTH", new TimeOnly(13, 30), "2", "C2"),
        new(FabrikamAirways, 52, "STH", "WFD", new TimeOnly(7, 20), "2", "B1"),
        new(FabrikamAirways, 77, "EVL", "LKS", new TimeOnly(11, 55), "1", "A1"),
        new(FabrikamAirways, 903, "WFD", "NTH", new TimeOnly(18, 5), "3", "D4"),
        new(TailspinAirlines, 410, "NTH", "WFD", new TimeOnly(8, 0), "3", "D1"),
        new(TailspinAirlines, 615, "LKS", "STH", new TimeOnly(15, 40), "2", "C5"),
    ];

    public static IReadOnlyList<string> DelayReasons { get; } =
    [
        "Late inbound aircraft",
        "Crew scheduling",
        "Weather at destination",
        "Air traffic control restrictions",
        "Technical inspection",
    ];

    public static IReadOnlyList<string> CancellationReasons { get; } =
    [
        "Operational reasons",
        "Severe weather",
        "Aircraft unserviceable",
    ];
}
