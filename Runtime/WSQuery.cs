using System;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class WSQuery : WebSocketBehavior
{
    public delegate void MessageEvent(string message);
    public MessageEvent messageReceived;

    public delegate void DataEvent(byte[] data);
    public DataEvent dataReceived;

    protected override void OnOpen()
    {
        Debug.Log("Socket Opened ");

    }
    protected override void OnClose(CloseEventArgs e)
    {
        Debug.Log("Socket closed " + e.Reason);

    }
    protected override void OnError(ErrorEventArgs e)
    {
        Debug.LogWarning("Socket error " + e.Message);
    }

    protected override void OnMessage(MessageEventArgs e)
    {
        var name = Context.QueryString["name"];
        if (e.IsText) messageReceived?.Invoke(e.Data);
        else if (e.IsBinary) dataReceived?.Invoke(e.RawData);
    }
}