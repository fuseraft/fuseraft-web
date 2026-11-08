---
title: Notifications
description: fuseraft-web tells you, in the browser, by email or through a webhook, when something needs your answer, when work finishes, and when a chat replies.
---

fuseraft-web tells you when:

- an approval or a chat's question is waiting for you,
- a session or a daemon task finishes or fails,
- a [scheduled job](#scheduled-jobs) fails, or succeeds if you ask,
- a chat replies.

While you're on another page, a note appears in the corner. Click it to go to what it's about.
Approvals and questions stay until you deal with them; the rest fade after a few seconds. On
the page it's about, there's no note.

## Desktop notifications

On the **Account** page, turn on **Desktop notifications** to hear about these while you're in
another tab or app. The tab's title shows how many you've missed until you come back.

Browsers show desktop notifications only for sites served over HTTPS, or from `localhost`. If
you've blocked them for the site, allow them again in the browser's site settings.

The same page lets you turn off any of the three groups. These choices are kept in each browser,
so set them on each device.

These come from your open fuseraft-web tabs, so with every tab closed nothing arrives. For that,
there's email, and webhooks.

## Email

Once an admin [sets up email](#setting-up-email), the **Account** page has an **Email** section:

- **Add an address**, and open the link emailed to it while signed in. The link works once, for 24
  hours. Until it's confirmed, nothing is sent there.
- If you sign in with single sign-on, **Use** the address your provider confirmed.
- An admin can also set someone's address on the **Users** page.

You're emailed when:

- an approval or a chat's question has waited 5 minutes unanswered. If you answer it in time,
  there's no email.
- a session or daemon task finishes or fails.
- a [scheduled job](#scheduled-jobs) fails, or succeeds if you ask.

Chat replies are never emailed. What happens within a minute arrives as one email. Each part can be
turned off on the Account page, and **Send a test email** checks that mail gets through.

Emails carry a title, a one-line summary and a link back. Never command output or file contents.

### Setting up email

Point `Fuseraft:Email` at an SMTP server, and set `Fuseraft:PublicUrl` so emails can link back:

```json
{
  "Fuseraft": {
    "PublicUrl": "https://fuseraft.example.com",
    "Email": {
      "Host": "smtp.example.com",
      "From": "fuseraft <fuseraft@example.com>",
      "UserName": "fuseraft@example.com"
    }
  }
}
```

The password goes in the `FUSERAFT_WEB_SMTP_PASSWORD` environment variable. Every setting is in
[Configuration](/fuseraft-web/reference/config/#email).

## Webhooks

The **Webhooks** page sends your notifications to a URL the moment they happen. There's no wait,
unlike email. Pick a format:

- **Slack message** posts `{"text": "…"}`, which Slack's incoming webhooks and Mattermost take as
  is. Discord takes it at the webhook's URL with `/slack` added.
- **JSON, signed** posts the notification for your own service to act on:

```json
{
  "type": "job.failed",
  "id": "5f0c…",
  "at": "2026-11-07T22:08:41+00:00",
  "title": "Scheduled job nightly failed (exit 1)",
  "body": "Run a security audit of the codebase",
  "link": "https://fuseraft.example.com/schedule",
  "category": "results",
  "level": "err"
}
```

`type` is one of `approval`, `question`, `session.finished`, `session.failed`, `task.finished`,
`task.failed`, `job.finished`, `job.failed`, `chat.replied`, `chat.failed` and `test`. `link` is a
full URL once an admin sets `Fuseraft:PublicUrl`, otherwise a path.

Each request is signed the [Standard Webhooks](https://www.standardwebhooks.com/) way, with
`webhook-id`, `webhook-timestamp` and `webhook-signature` headers. Check them with any Standard
Webhooks library and the webhook's secret (**Show secret**), and turn away requests that don't
match or are more than a few minutes old. **New secret** replaces it. Webhook URLs and secrets are
stored encrypted with the server's data-protection keys.

Each webhook chooses which groups it gets: approvals and questions, results, chat replies. A
receiver that's down or answers `5xx` or `429` is tried again after 5 seconds, 30 seconds and 2
minutes. An approval answered meanwhile isn't sent again. The page shows each webhook's last few
deliveries, and **Send a test** checks one. Like email, requests carry a title, a one-line
summary and a link, never command output or file contents.

### Where webhooks may point

Anyone can add up to 10 webhooks for their own notifications. So that nobody can use one to reach
the server's own machine or network, a webhook must use `https` and reach the public internet.
Addresses on this machine, private networks, link-local addresses (including cloud metadata) and
the like are refused, both when it's saved and on every connection. Redirects aren't followed, and
no proxy is used.

To let webhooks reach a service on your network, an admin lists its host in
`Fuseraft:Webhooks:AllowedHosts`. Plain `http` is allowed for those hosts too. Set
`Fuseraft:Webhooks:AdminsOnly` to keep webhooks to admins, or `Fuseraft:Webhooks:Enabled` to
`false` to turn them off. See [Configuration](/fuseraft-web/reference/config/#webhooks).

## Scheduled jobs

Each job on the **Schedule** page has a **Notify me of** choice: **Failures**, **Successes**, **Both**
or **Nothing**. It starts at Failures, also for jobs added with `fuseraft schedule add`.

A job's run is reported when fuseraft-web ran it, which it does once a minute unless
[`Fuseraft:RunSchedules`](/fuseraft-web/reference/config/) is off. Runs started by the host's own cron
aren't reported, and neither are runs from fuseraft before 1.21, which doesn't record how a job went.
**Run** on the Schedule page starts a live session, which notifies like any other.

## Who is told

The same people, in the browser, by email and through webhooks:

- You hear about your own sessions, chats, daemon tasks and scheduled jobs.
- Admins also hear about other people's session approvals, which they can answer, and about
  approvals and tasks on a daemon whose sender isn't known. They don't hear about other people's
  chats or results.
