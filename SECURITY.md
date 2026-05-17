# Security Policy

## Supported Branches
Security fixes are developed on `main`.  
When `stable` exists, critical fixes are patched to `stable` first, then backported to `main`.

## Reporting a Vulnerability
Please do **not** open public issues for vulnerabilities.

Report privately with:
- GitHub Security Advisory (preferred), or
- Reddit DM: u/Ed_loaqx

Include:
- Affected component/file
- Reproduction steps
- Impact
- Suggested fix (optional)

## Response Targets
- Initial acknowledgment: within 72 hours
- Triage decision: within 7 days
- Critical fix target: as soon as safely testable

## Disclosure Policy
- We follow coordinated disclosure.
- Public disclosure happens after fix is available or mitigation is documented.

## Secrets and Credentials
- Never commit secrets/tokens/passwords.
- Use environment variables or secret stores.
- Rotate exposed credentials immediately.

## Secure Development Rules
- PR required for `main`
- Required checks: build, tests, CodeQL, Sonar
- No direct pushes to protected branches
- No force-push on protected branches

## Scope
In scope:
- Authentication/authorization flaws
- Injection flaws
- CSRF/XSS
- Secret leakage
- Dependency vulnerabilities
- Privilege escalation

Out of scope:
- Best-practice suggestions without exploitability
- Issues requiring unrealistic attacker assumptions
