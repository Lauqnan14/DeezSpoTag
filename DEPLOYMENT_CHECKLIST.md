# Deployment Checklist

- [ ] Set `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] Set `DEEZSPOTAG_BOOTSTRAP_USER` and `DEEZSPOTAG_BOOTSTRAP_PASS` as environment variables.
- [ ] Do not set non-empty `LoginConfiguration:Password` in production appsettings files.
- [ ] Set `DEEZSPOTAG_API_FRONTEND_ORIGIN` to the exact frontend origin.
- [ ] Verify TLS is enabled and certificates are valid.
- [ ] Verify logs do not contain secrets or connection strings.
- [ ] Verify rate limiting policies are enabled for authentication and sensitive write endpoints.
- [ ] Validate reverse-proxy forwarding/trust settings before exposing local-only APIs.
