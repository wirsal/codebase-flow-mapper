public class PayRequest
{
    public string Channel { get; set; }
    public string Currency { get; set; }
    public decimal Amount { get; set; }
    public bool Express { get; set; }
    public Account Source { get; set; }
}

public class Account
{
    public string Bank { get; set; }
    public string Number;
}

public class PayResponse
{
    public string Code { get; set; }
    public string Message { get; set; }
}

public class InquiryRequest
{
    public string Kind { get; set; }
    public int Tenor { get; set; }
}
