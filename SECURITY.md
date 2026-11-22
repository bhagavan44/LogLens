# Security Policy

## Supported versions

Only the latest release of LogLens receives security fixes. Please update before reporting a
problem.

## Reporting a vulnerability

Please **don't report security problems in public issues, pull requests or discussions.**

Report them privately through GitHub:

1. Go to the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the problem, how to reproduce it, and what an attacker could do with it.

You should get a first response within a week. Once the problem is confirmed, a fix will be
prepared and released, and you will be credited in the advisory unless you prefer otherwise.

## Scope

LogLens is an offline Windows desktop app that only reads log files. Logs often contain personal
data and secrets, so issues of particular interest include:

- A crafted log file that runs code, crashes the app, or hangs it (for example through
  catastrophic regex backtracking or deeply nested JSON)
- LogLens writing to, locking, truncating or deleting a log file it opened
- Log content, settings or file paths sent anywhere over the network
- Exported files (HTML in particular) that run script from log content when opened
- Vulnerable dependencies that are reachable from the app

Out of scope: problems that require an already compromised machine or user account, and attacks
that need the victim to run a modified build of LogLens.
