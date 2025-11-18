public abstract class PaymentBase
{
    public bool IsError;
    public delegate void Payment(int errorCode);

    public new delegate void PartPayment(int sum);

    public event Payment? ResultEvent;
    public event PartPayment? PartPaymentEvent;
    public event Action? ErrorEvent;
    public abstract void CloseSession();

    protected void OnResult(int errorCode)
    {
        ResultEvent?.Invoke(errorCode);
    }

    protected void OnPartResult(int credit)
    {
        PartPaymentEvent?.Invoke(credit);
    }

    protected void OnError()
    {
        ErrorEvent?.Invoke();
    }
}