public interface INotifier
{
    void Notify(string message);
}

// Wired: PaymentController.notifier is only ever assigned new SmsNotifier().
public class SmsNotifier : INotifier
{
    public void Notify(string message)
    {
        EsbClient.Post("notify.sms", message);
    }
}

// Decoy: implements INotifier but is never assigned to PaymentController.notifier.
public class EmailNotifier : INotifier
{
    public void Notify(string message)
    {
        EsbClient.Post("notify.email", message);
    }
}
