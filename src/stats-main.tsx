import React from "react";
import ReactDOM from "react-dom/client";
import { StatsWindow } from "./analytics/StatsWindow";
import { ErrorBoundary, installGlobalErrorLogging } from "./ErrorBoundary";
import "./index.css";

installGlobalErrorLogging("stats");

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ErrorBoundary name="stats">
      <StatsWindow />
    </ErrorBoundary>
  </React.StrictMode>
);
