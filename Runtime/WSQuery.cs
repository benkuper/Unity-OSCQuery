using System;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class WSQuery : WebSocketBehavior
{
    public delegate void MessageEvent(WSQuery query,string message);
    public MessageEvent messageReceived;

    public delegate void DataEvent(WSQuery query, byte[] data);
    public DataEvent dataReceived;

    public delegate void WSQueryEvent(WSQuery query);
    public WSQueryEvent socketOpened;
    public WSQueryEvent socketClosed;
    public WSQueryEvent socketError;

    public void sendMessage(string message)
    {
        Send(message);
    }
    
    public void sendData(byte[] data)
    {
        Send(data);
    }

    protected override void OnOpen()
    {
        Debug.Log("Socket Opened ");
        socketOpened?.Invoke(this);

    }
    protected override void OnClose(CloseEventArgs e)
    {
        Debug.Log("Socket closed " + e.Reason);
        socketClosed?.Invoke(this);
    }
    protected override void OnError(ErrorEventArgs e)
    {
        Debug.LogWarning("Socket error " + e.Message);
        socketError?.Invoke(this);
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        var name = Context.QueryString["name"];
        if (e.IsText) messageReceived?.Invoke(this, e.Data);
        else if (e.IsBinary) dataReceived?.Invoke(this, e.RawData);
    }

}