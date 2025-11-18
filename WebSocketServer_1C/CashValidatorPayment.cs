using System.Collections.Generic;
using System.Threading.Tasks;
using CashCode;

using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Timers;
using CashCode;
using WebSocketServer_1C;

public class CashValidatorPayment : PaymentBase
{
    private bool _isClosed;
    private bool _isErrorHandled;

    private bool _isDisposing = false;

    private int _sum = 0;
    private long _needSum = 0;
    private readonly CashCodeBillValidator _cashCodeBV;
    private readonly object _disposeLock = new();
    System.Timers.Timer pingTimer;

    public CashValidatorPayment(string portName, long targetSum, int baudRate = 9600, Dictionary<int, int>? cashCodeTable = null)
    {
        _needSum = targetSum;
        _cashCodeBV = new CashCodeBillValidator(portName, baudRate);
        _cashCodeBV.ConnectBillValidator();

        try
        {
            _cashCodeBV.PowerUpBillValidator();
        }
        catch
        {
            _cashCodeBV.Dispose();
            throw;
        }
        
        _cashCodeBV.BillReceived += CashCodeBvOnBillReceived;
        _cashCodeBV.StartListening();
        StartPolling();

        FileLogger.Log("[CASH] Created");
    }

    private void StartPolling()
    {
        pingTimer = new(1000);
        pingTimer.Elapsed += PingTimerHandler;
        pingTimer.Start();
    }

    private void StopPolling()
    {
        pingTimer.Stop();
        pingTimer.Elapsed -= PingTimerHandler;
        pingTimer.Dispose();
    }

    void PingTimerHandler(object sender, ElapsedEventArgs e)
    {
        pingTimer.Stop();

        lock (_disposeLock)
        {
            if (_isDisposing)
            {
                pingTimer.Start();
                return;
            }

            var status = _cashCodeBV.EnableBillValidator();
            pingTimer.Start();

            if (status != 0)
            {
                _isDisposing = true;
                StopPolling();
                Console.WriteLine(_cashCodeBV.IsConnected);

                _cashCodeBV.BillReceived -= CashCodeBvOnBillReceived;
                _cashCodeBV.Dispose();

                FileLogger.Log("[CASH] Disabled by error");

                if (!_isErrorHandled)
                {
                    _isErrorHandled = true;
                    _isClosed = true;
                    OnError();
                }
            }
        }
    }

    private void CashCodeBvOnBillReceived(object sender, BillReceivedEventArgs e)
    {
        if (e.Status != BillRecievedStatus.Accepted) return;
        _sum += e.Value;
        OnPartResult(e.Value);
    }

    public override void CloseSession()
    {
        lock (_disposeLock)
        {
            if (_isClosed || _isDisposing) return;

            _isDisposing = true;
            StopPolling();

            try
            {
                _cashCodeBV.DisableBillValidator();
                _cashCodeBV.StopListening();
                _cashCodeBV.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CASH] Error during close: {ex.Message}");
            }

            _isClosed = true;
            FileLogger.Log("[CASH] Closed");
        }
    }
}