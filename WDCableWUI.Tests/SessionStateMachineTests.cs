using Microsoft.VisualStudio.TestTools.UnitTesting;
using WDCableWUI.Services;

namespace WDCableWUI.Tests;

[TestClass]
public sealed class SessionStateMachineTests
{
    [TestMethod]
    public void ReadyPathTransitionsInOrder()
    {
        var stateMachine = new SessionStateMachine();

        stateMachine.TransitionTo(SessionPhase.WifiDirectConnected);
        stateMachine.TransitionTo(SessionPhase.ConnectingTransport);
        stateMachine.TransitionTo(SessionPhase.Handshaking);
        stateMachine.TransitionTo(SessionPhase.Ready);

        Assert.AreEqual(SessionPhase.Ready, stateMachine.Phase);
    }

    [TestMethod]
    public void DuplicateTransitionIsIdempotent()
    {
        var stateMachine = new SessionStateMachine();

        stateMachine.TransitionTo(SessionPhase.WifiDirectConnected);
        stateMachine.TransitionTo(SessionPhase.WifiDirectConnected);

        Assert.AreEqual(SessionPhase.WifiDirectConnected, stateMachine.Phase);
    }

    [TestMethod]
    public void InvalidTransitionThrows()
    {
        var stateMachine = new SessionStateMachine();

        Assert.ThrowsException<InvalidOperationException>(() => stateMachine.TransitionTo(SessionPhase.Ready));
    }

    [TestMethod]
    public void FailedSessionCanResetToDisconnected()
    {
        var stateMachine = new SessionStateMachine();

        stateMachine.TransitionTo(SessionPhase.WifiDirectConnected);
        stateMachine.TransitionTo(SessionPhase.Failed);
        stateMachine.TransitionTo(SessionPhase.Disconnected);

        Assert.AreEqual(SessionPhase.Disconnected, stateMachine.Phase);
    }
}
