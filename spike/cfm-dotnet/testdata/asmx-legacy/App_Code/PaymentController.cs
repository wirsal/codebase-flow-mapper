using System.Net;

public class PaymentController
{
    INotifier notifier = new SmsNotifier();
    EsbClient esb = new EsbClient();

    public PayResponse Pay(PayRequest req)
    {
        PayResponse res = new PayResponse();
        if (string.IsNullOrEmpty(req.Source.Bank))
        {
            res.Code = "01";
            return res;
        }
        if (req.Express && req.Amount > 1000)
        {
            notifier.Notify("express payment");
        }
        switch (req.Currency.ToUpper())
        {
            case "IDR":
                res.Code = esb.Send("pay.domestic", req.Channel);
                break;
            case "USD":
            case "SGD":
                res.Code = esb.Send("pay.foreign", req.Channel);
                break;
            default:
                return Reject(res, "currency");
        }
        try
        {
            AuditLogs.Create().Write(res.Code);
        }
        catch (WebException)
        {
            return Reject(res, "audit");
        }
        return res;
    }

    public string Status(string id)
    {
        return esb.Send("pay.status", id);
    }

    PayResponse Reject(PayResponse res, string reason)
    {
        res.Code = "99";
        res.Message = reason;
        return res;
    }
}
