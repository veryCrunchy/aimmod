import React from "react";
import { logError } from "./log";

/**
 * Catches render errors so a failing component shows its error instead of
 * blanking the whole window, and forwards the error to aimmod.log.
 */
export class ErrorBoundary extends React.Component<
  { name: string; children: React.ReactNode },
  { error: Error | null }
> {
  state: { error: Error | null } = { error: null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    logError(
      this.props.name,
      `render error: ${error.message}\n${error.stack ?? ""}\ncomponent stack:${info.componentStack ?? ""}`,
    );
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    return (
      <div style={{ padding: 24, color: "#f5f5f5", background: "#111", minHeight: "100vh", fontFamily: "monospace" }}>
        <h2 style={{ marginBottom: 12 }}>Something in this view crashed</h2>
        <p style={{ marginBottom: 12, color: "#ff8a8a" }}>{error.message}</p>
        <pre style={{ whiteSpace: "pre-wrap", fontSize: 12, opacity: 0.8 }}>{error.stack}</pre>
        <button
          style={{ marginTop: 16, padding: "6px 14px", cursor: "pointer" }}
          onClick={() => this.setState({ error: null })}
        >
          Try again
        </button>
      </div>
    );
  }
}

/** Forwards uncaught errors and rejections from this window to aimmod.log. */
export function installGlobalErrorLogging(name: string) {
  window.addEventListener("error", (event) => {
    logError(name, `uncaught: ${event.message} @ ${event.filename}:${event.lineno}:${event.colno}\n${event.error?.stack ?? ""}`);
  });
  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason instanceof Error ? `${event.reason.message}\n${event.reason.stack ?? ""}` : String(event.reason);
    logError(name, `unhandled rejection: ${reason}`);
  });
}
