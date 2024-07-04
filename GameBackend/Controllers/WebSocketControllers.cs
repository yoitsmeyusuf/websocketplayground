using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace WebSocketsSample.Controllers;

#region snippet_Controller_Connect
public class WebSocketController : ControllerBase
{
    [Route("/ws")]
    public async Task Get()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await Echo(webSocket);
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    #endregion

private static ConcurrentDictionary<Guid, WebSocket> activeConnections = new ConcurrentDictionary<Guid, WebSocket>();
private static ConcurrentDictionary<Guid, List<string>> movementCommands = new ConcurrentDictionary<Guid, List<string>>();
private static async Task Echo(WebSocket webSocket)
{
    Guid connectionId = Guid.NewGuid();
    activeConnections.TryAdd(connectionId, webSocket);

    var idBytes = Encoding.UTF8.GetBytes($"Connection ID: {connectionId}");
    await webSocket.SendAsync(new ArraySegment<byte>(idBytes), WebSocketMessageType.Text, true, CancellationToken.None);

    using var cts = new CancellationTokenSource();
    var token = cts.Token;

    // Timer to track inactivity and disconnect after 30 seconds
    using var inactivityTimer = new Timer(async _ =>
    {
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Inactivity timeout", CancellationToken.None);
        }
    }, null, Timeout.Infinite, Timeout.Infinite); // Initially disabled

    var buffer = new byte[1024 * 4];
    WebSocketReceiveResult receiveResult;

    do
    {
        // Reset the inactivity timer every time before waiting for a message
        inactivityTimer.Change(30000, Timeout.Infinite);

        receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        // Stop the inactivity timer when a message is received
        inactivityTimer.Change(Timeout.Infinite, Timeout.Infinite);

        await ProcessReceivedMessage(webSocket, receiveResult, buffer);
    }
    while (!receiveResult.CloseStatus.HasValue);

    // Cancel the inactivity timer as the connection is closing
    inactivityTimer.Change(Timeout.Infinite, Timeout.Infinite);

    // Close the WebSocket connection
    await webSocket.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);

    // Remove the connection from the active connections
    activeConnections.TryRemove(connectionId, out _);
}

private static async Task ProcessReceivedMessage(WebSocket webSocket, WebSocketReceiveResult receiveResult, byte[] buffer)
{
    var receivedText = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
    var modifiedText = receivedText;
    if (receivedText == "ping")
    {
        modifiedText = "pong";
    }
    if (receivedText == "W" || receivedText == "A" || receivedText == "S" || receivedText == "D")
    {
        modifiedText = receivedText;
        // store the movement command in a dictionary
        movementCommands.AddOrUpdate(activeConnections.First(pair => pair.Value == webSocket).Key,new List<string> { receivedText }, (key, existingList) =>
        {
            existingList.Add(receivedText);
            return existingList;
        });
    }
    
    if(Guid.TryParse(receivedText, out Guid guid) && activeConnections.ContainsKey(guid)){
        // send the movement command to the client
        var movementCommandBytes = Encoding.UTF8.GetBytes(string.Join(" ", movementCommands[guid]));
        await webSocket.SendAsync(new ArraySegment<byte>(movementCommandBytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    var modifiedBytes = Encoding.UTF8.GetBytes(modifiedText);

    if (!receiveResult.CloseStatus.HasValue)
    {
        await webSocket.SendAsync(new ArraySegment<byte>(modifiedBytes), receiveResult.MessageType, receiveResult.EndOfMessage, CancellationToken.None);
    }
}


}
