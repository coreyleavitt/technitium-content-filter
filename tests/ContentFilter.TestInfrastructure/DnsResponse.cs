namespace ContentFilter.TestInfrastructure;

/// <summary>Parsed DNS response.</summary>
public record DnsResponse(byte ResponseCode, List<DnsAnswer> Answers)
{
    public bool IsNxDomain => ResponseCode == 3;
    public bool IsNoError => ResponseCode == 0;
    public DnsAnswer? FirstAnswer => Answers.Count > 0 ? Answers[0] : null;
    public string? CnameTarget => Answers.Where(a => a.Type == 5).Select(a => a.Data).FirstOrDefault();
}

/// <summary>Parsed DNS answer record.</summary>
public record DnsAnswer(ushort Type, uint Ttl, string Data)
{
    public bool IsA => Type == 1;
    public bool IsCname => Type == 5;
    public bool IsAaaa => Type == 28;
}
