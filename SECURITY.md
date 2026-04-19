# Security policy

## Supported versions

Only the latest release published to the custom Dalamud repo is supported. If you're on an older `vX.Y.Z`, please update before reporting.

## Reporting a vulnerability

Please report security issues privately via GitHub's private vulnerability reporting:

https://github.com/XeldarAlz/FFXIV-MahjongAI/security/advisories/new

Please don't open a public issue or Discussion for anything that could let someone else exploit users of the plugin before a fix is out.

What counts:

- Code execution or crashes triggerable by crafted game state or addon contents.
- The plugin clicking, sending, or persisting something it shouldn't (beyond the documented "against TOS" auto-play behavior, which is the feature, not a bug).
- Data exfiltration — this plugin makes no network calls by design; any network traffic would be a bug.

What doesn't:

- The fact that auto-play mode sends simulated input, which is against Square Enix's Terms of Service. This is documented and intended behavior; turning it on is the user's choice. See the Safety section of the README.

I'll aim to acknowledge reports within a few days and to ship a fix or workaround as soon as I've verified the issue.
