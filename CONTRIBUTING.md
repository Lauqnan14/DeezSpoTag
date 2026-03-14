# Contributing

## Workflow

1. Fork the repository.
2. Create a branch from `main`.
3. Keep changes focused and small.
4. Add or update tests for behavior changes.
5. Open a pull request with:
   - clear summary
   - repro/validation steps
   - related issue reference (if any)

## Local Validation

```bash
dotnet restore src.sln
dotnet build src.sln -c Debug
dotnet test src.sln -c Debug
```

## Notes

- Do not commit secrets, account blobs, runtime DB files, or local `.env`.
- Keep Docker and local run paths aligned with `README.md`.
