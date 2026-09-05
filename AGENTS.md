# Publication Privacy

- Never commit or push design-QA reports, local captures, diagnostics, private replay or score data, or machine-specific paths.
- Keep QA output in ignored local directories. Do not force-add ignored artifacts.
- Use environment-derived paths in code and synthetic identities in tests.
- Before every commit and push, inspect the staged diff and file list for private data and local filesystem references.
- Public product screenshots must be deliberately selected and reviewed for private information, never collected from QA output automatically.
