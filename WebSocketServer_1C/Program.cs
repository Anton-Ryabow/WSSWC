using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.InteropServices.JavaScript;
using CashCode;
using WebSocketServer_1C;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Urls.Add(builder.Configuration["Host"]);

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

//List<Session> sessions = [];
Session? session = null;

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Endpoint only accepts WebSocket requests.");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    FileLogger.Log("[SERVER] Client connected: " + context.Connection.RemoteIpAddress);
    
    var buffer = new byte[4 * 1024];
    var ct = context.RequestAborted;
    
    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", ct);
                    FileLogger.Log("[SERVER] Client closed connection.");
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(ms.ToArray());
                FileLogger.Log("[SERVER] Received: " + message);
                try
                {
                    var jsonDoc = JsonDocument.Parse(message);
                    var root = jsonDoc.RootElement;
                    if (root.TryGetProperty("Command", out var cmdElement))
                    {
                        string command = cmdElement.GetString() ?? "";
                        if (command == "ValidatorStart")
                        {
                            if (session is not null)
                                session.CloseSession();
                            session = new Session(root, webSocket, ct, builder.Configuration["COM:cash"], builder.Configuration["COM:coin"]);
                        }
                        else if (command == "ValidatorStop")
                        {
                            FileLogger.Log("[SERVER] ValidatorStop received");
                            session?.CloseSession();
                        }
                    }
                }
                catch (TimeoutException ex)
                {
                    FileLogger.Log("[SERVER] Validator is unavailable: " + ex.Message);
                }
                catch (JsonException ex)
                {
                    FileLogger.Log("[SERVER] JSON parse or processing error: " + ex.Message);
                }
            }
        }
    }
    catch (OperationCanceledException)
    {
        
    }
    catch (WebSocketException ex)
    {
        FileLogger.Log("[SERVER] WebSocket error: " + ex.Message);
        session?.CloseSession();
    }
});
app.Run();

class Session
{
    private bool _isClosed;

    private Guid _guid;
    private readonly int _targetSum;
    private CancellationToken ct;
    private int _currentSum = 0;
    private JsonElement _root;
    private WebSocket _webSocket;
    private CashValidatorPayment _validator;
    private CoinValidatorPayment _coinValidator;
    public Session(JsonElement root, WebSocket webSocket, CancellationToken token, string cashPort, string coinPort)
    {
        _root = root;
        _webSocket = webSocket;
        _targetSum = root.GetProperty("Sum").GetInt32();
        _guid = root.GetProperty("Guid").GetGuid();
        ct = token;

        ErrorEvent += async (error) =>
        {
            if (error == SessionError.Cash)
            {
                var errorMessage = new { Status = "CashCodeDisconnected", Guid = _guid };
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorMessage));
                await webSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
                if (_validator != null) _validator.IsError = true;
                _validator?.CloseSession();
                if (_coinValidator != null && _coinValidator.IsError)
                {
                    var fullErrorMessage = new { Status = "DevicesDisconnected", Guid = _guid };
                    var fullErrorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fullErrorMessage));
                    await webSocket.SendAsync(fullErrorBytes, WebSocketMessageType.Text, true, ct);
                    CloseSession();
                }
            }
            else if (error == SessionError.Coin)
            {
                var errorMessage = new { Status = "MicrocoinDisconnected", Guid = _guid };
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorMessage));
                await webSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
                if (_coinValidator != null) _coinValidator.IsError = true;
                _coinValidator?.CloseSession();
                if (_validator == null || _validator.IsError)
                {
                    var fullErrorMessage = new { Status = "DevicesDisconnected", Guid = _guid };
                    var fullErrorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fullErrorMessage));
                    await webSocket.SendAsync(fullErrorBytes, WebSocketMessageType.Text, true, ct);
                    CloseSession();
                }
            }
        };
        try
        {
            _validator = new CashValidatorPayment(cashPort, _targetSum);
        }
        catch(Exception ex)
        {
            ErrorEvent?.Invoke(SessionError.Cash);
        }
        try
        {
            _coinValidator = new CoinValidatorPayment(coinPort);
        }
        catch (Exception ex)
        {
            ErrorEvent?.Invoke(SessionError.Coin);
        }
        
        if (_validator != null)
        {
            _validator.PartPaymentEvent += async (int amount) =>
            {
                _currentSum += amount;
                var response = new { DateTime = DateTime.Now, Guid = _guid, Accepted = amount, Device = "CashCode"};
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                FileLogger.Log($"[SESSION] Accepted {amount} for {_guid}");
                if (_currentSum < _targetSum) return;
                var final = new { Status = "FullSumAccepted", Guid = _guid };
                var finalBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(final));
                await webSocket.SendAsync(finalBytes, WebSocketMessageType.Text, true, ct);
                FileLogger.Log($"[SESSION] Full sum accepted for {_guid}");
                CloseSession();
            };
            _validator.ErrorEvent += async () =>
            {
                var errorMessage = new { Status = "CashCodeDisconnected", Guid = _guid };
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorMessage));
                await webSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
                _validator.IsError = true;
                _validator.CloseSession();
                if (_coinValidator == null || _coinValidator.IsError)
                {
                    var fullErrorMessage = new { Status = "DevicesDisconnected", Guid = _guid };
                    var fullErrorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fullErrorMessage));
                    await webSocket.SendAsync(fullErrorBytes, WebSocketMessageType.Text, true, ct);
                    CloseSession();
                }
            };
        }

        if (_coinValidator != null)
        {
            _coinValidator.ErrorEvent += async () =>
            {
                var errorMessage = new { Status = "MicroCoinDisconnected", Guid = _guid };
                var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(errorMessage));
                await webSocket.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
                _coinValidator.IsError = true;
                _coinValidator.CloseSession();
                if (_validator == null || _validator.IsError)
                {
                    var fullErrorMessage = new { Status = "DevicesDisconnected", Guid = _guid };
                    var fullErrorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fullErrorMessage));
                    await webSocket.SendAsync(fullErrorBytes, WebSocketMessageType.Text, true, ct);
                    CloseSession();
                }
            };
            _coinValidator.PartPaymentEvent += async (int amount) =>
            {
                _currentSum += amount;
                var response = new { DateTime = DateTime.Now, Guid = _guid, Accepted = amount, Device = "Microcoin" };
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                FileLogger.Log($"[SESSION] Accepted {amount} for {_guid}");
                if (_currentSum < _targetSum) return;
                var final = new { Status = "FullSumAccepted", Guid = _guid };
                var finalBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(final));
                await webSocket.SendAsync(finalBytes, WebSocketMessageType.Text, true, ct);
                FileLogger.Log($"[SESSION] Full sum accepted for {_guid} (from micro)");
                CloseSession();
            };
        }
        FileLogger.Log("[SESSION] Created");
    }
    public void CloseSession()
    {
        if (_isClosed) return;
        _validator?.CloseSession();
        _coinValidator?.CloseSession();
        _isClosed = true;
        FileLogger.Log("[SESSION] Closed");
    }
    
    public delegate void ErrorHandler(SessionError error);
    public event ErrorHandler ErrorEvent;
}

internal enum SessionError
{
    Cash,
    Coin
}