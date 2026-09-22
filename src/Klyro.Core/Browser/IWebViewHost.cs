using System;

namespace Klyro.Core.Browser;

/// <summary>
/// Abstraction for WebView2 hosting boundaries (Exam and Recovery).
/// </summary>
public interface IWebViewHost : IDisposable
{
    string CurrentUrl { get; }
    bool IsInitialized { get; }
    bool IsActive { get; }

    void Navigate(string url);
    void Pause();
    void Resume();
}
