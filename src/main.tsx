import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { ErrorBoundary, installGlobalErrorLogging } from "./ErrorBoundary";
import "./index.css";

installGlobalErrorLogging("main");

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ErrorBoundary name="main">
      <App />
    </ErrorBoundary>
  </React.StrictMode>
);
