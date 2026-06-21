namespace CcDirector.Core.Account;

/// <summary>
/// The startup gate's decision (issue #580): whether the Director may reach a usable main window
/// on this install, or must instead block and route the user to log in.
/// </summary>
public enum GateDecision
{
    /// <summary>
    /// No credential has ever been stored on this install. The Director must not reach a usable
    /// main window; it shows the account gate screen that routes the user to log in (the login
    /// hand-off itself is issue #581).
    /// </summary>
    Block,

    /// <summary>
    /// A cached credential exists. The Director starts normally to the main window, online or
    /// offline. When online, a background validation or refresh runs without blocking the window.
    /// </summary>
    Start,
}
