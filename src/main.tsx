import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { ErrorBoundary, installGlobalErrorLogging } from "./ErrorBoundary";
import "./index.css";

installGlobalErrorLogging("main");

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    {/* The overlay sits over the game and ignores clicks: hide errors (they
        are still logged) rather than cover the game with an error panel. */}
    <ErrorBoundary name="main" hidden>
      <App />
    </ErrorBoundary>
  </React.StrictMode>
);
