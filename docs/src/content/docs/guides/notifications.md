---
title: Notifications
description: fuseraft-web tells you, in the browser or by email, when something needs your answer, when work finishes, and when a chat replies.
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
there's email.

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

## Scheduled jobs

Each job on the **Schedule** page has a **Notify me of** choice: **Failures**, **Successes**, **Both**
or **Nothing**. It starts at Failures, also for jobs added with `fuseraft schedule add`.

A job's run is reported when fuseraft-web ran it, which it does once a minute unless
[`Fuseraft:RunSchedules`](/fuseraft-web/reference/config/) is off. Runs started by the host's own cron
aren't reported, and neither are runs from fuseraft before 1.21, which doesn't record how a job went.
**Run** on the Schedule page starts a live session, which notifies like any other.

## Who is told

The same people, in the browser and by email:

- You hear about your own sessions, chats, daemon tasks and scheduled jobs.
- Admins also hear about other people's session approvals, which they can answer, and about
  approvals and tasks on a daemon whose sender isn't known. They don't hear about other people's
  chats or results.
