namespace MerlinSip.Models;

using System;
using MerlinSip.Services;

public enum CallWaitingAction
{
    None,
    HoldAndAnswer,
    Reject,
    SendToVoicemail
}

/// <summary>
/// Event arguments for an incoming call when another call is already active.
/// </summary>
public class CallWaitingEventArgs : EventArgs
{
    /// <summary>
    /// Gets the caller's ID or number.
    /// </summary>
    public string CallerId { get; }

    /// <summary>
    /// Gets the internal reference to the pending call.
    /// </summary>
    internal PendingIncomingCall IncomingCall { get; }

    /// <summary>
    /// Gets or sets the action to take for this call waiting event.
    /// </summary>
    public CallWaitingAction SelectedAction { get; set; } = CallWaitingAction.None;

    internal CallWaitingEventArgs(string callerId, PendingIncomingCall incomingCall)
    {
        CallerId = callerId;
        IncomingCall = incomingCall;
    }
}
