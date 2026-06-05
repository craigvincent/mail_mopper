<p align="center">
  <img src="assets/mail_mopper_small_logo.png" alt="Mail Mopper" width="300" />
</p>

# 🧹 Mail Mopper

## CLI Janitor for Gmail inbox cleanup.

The Janitor's hybrid classification pipeline sorts through the mess — rules handle the obvious junk, a locally-trained classifier catches the rest — then lets you review everything in an interactive terminal UI before a single email hits the trash.

> *"Hey. Hey - stop walking. I know you saw me, Scooter.*
>
> *You know what I found in your inbox? Sixty thousand emails. Sixty. Thousand. I once found a raccoon living in the west wing air duct and that was less of a mess than this. You've got newsletters from 2014 in there. Coupons that expired during the Obama administration. LinkedIn notifications from people who are probably dead now. It's disgusting.*
>
> *So I built something. I call it the Mail Mopper. Part rules engine, part machine learning, part mop. I attached an algorithm to a bucket and taught it to sort junk. Took me three weekends and I had to learn C#, which, honestly, was easier than getting the knot out of the east stairwell drain last Tuesday.*
>
> *Here's how it works: I sweep through your inbox, sort the garbage from the not-garbage, and then you - yes, you - get to review it all before anything goes in the trash. Because I know how you are. You'll panic. You'll say 'oh no, what about my Pottery Barn receipt from 2019.' It'll still be in the trash for thirty days. Relax.*
>
> *Now are you gonna let me mop, or are you just gonna stand there holding that door?"*
>
> - **The Janitor**

---

## ✨ What the Mop Does

- 📬 **Fetches email metadata** (headers + snippet preview - I'm not reading your whole diary, Bambi) into a local database
- 🧠 **Hybrid classification**: rule-based engine slaps labels on the obvious junk, then a locally-trained classifier handles the rest - like having a smarter mop
- 🖥️ **Interactive review**: browse by category → sender → email subjects. You get final say, even though I'm always right
- 🛡️ **Safe**: moves to Trash only (recoverable 30 days). I'm a janitor, not a monster
- 🔄 **Incremental sync**: only fetches new emails on re-runs. I'm efficient, unlike Dr. Dorian
- 📋 **Full audit log** with undo support - because *somebody* always panics
- ⚡ **Handles 60,000+ emails** efficiently with batching and rate limiting. I've seen worse. Way worse.

## 📋 Prerequisites

- [Docker](https://docs.docker.com/get-docker/)
- Gmail API credentials (see below)

## 🔧 Setup

### 1. Google Cloud Console

Look, even a doctor could do this part:

1. Go to https://console.cloud.google.com/
2. Create a new project (or select existing)
3. Enable the **Gmail API**
4. Go to **Credentials** → Create **OAuth 2.0 Client ID** → Desktop application
5. Download the JSON credentials file
6. Save it as `credentials.json` in your working directory

### 2. Pull the image

```bash
docker pull ghcr.io/craigvincent/mail_mopper:latest
```

### 3. Authenticate

```bash
docker run -it --rm \
  -p 8484:8484 \
  -v gmail-data:/home/app/.local/share/MailMopper \
  -v "$(pwd)/credentials.json:/app/credentials.json:ro" \
  ghcr.io/craigvincent/mail_mopper auth
```

This prints a Google sign-in URL. Open it in your browser, authorise, and the callback is captured through the port mapping. Your token is saved in the `gmail-data` volume for all future commands.

## 🧹 Usage

For convenience, set an alias (or substitute the full `docker run` command each time):

```bash
alias mopper='docker run -it --rm \
  -v gmail-data:/home/app/.local/share/MailMopper \
  -v "$(pwd)/credentials.json:/app/credentials.json:ro" \
  ghcr.io/craigvincent/mail_mopper'
```

Show all available commands:

```bash
mopper --help
```

### The Full Mopping Procedure

Follow these steps in order, sport. I'm not explaining them twice.

```bash
mopper auth                  # 1. Badge in - authenticate with Gmail
mopper fetch                 # 2. Survey the mess - fetch email metadata
mopper classify --skip-ml    # 3. First pass with the push broom (rules only)
mopper train                 # 4. Teach the mop new tricks (train classifier)
mopper classify              # 5. Second pass - now with the smart mop
mopper review                # 6. YOU look at what I sorted. Don't mess it up
mopper execute               # 7. Take out the trash. My favorite part
```

### Other Janitor Commands

```bash
mopper stats                 # Admire my work - show statistics
mopper undo <id>             # Fine. Untrash a session. Quitter
mopper repair-dates          # Fix dates I got wrong the first time. I'm only human
mopper reset                 # Burn it all down. Start from scratch. No, really, are you sure?
mopper run                   # Full pipeline - let me handle everything
```

### Key Flags

- `--skip-ml` - Skip ML classification, just use rules. Sometimes the push broom is enough
- `--dry-run` - Preview what would happen without making changes. For the nervous types
- `--full` - Force full fetch instead of incremental. Start over from scratch, like a fresh floor

> **Note**: The `-it` flags are required for the interactive `review` command.

## 🧠 Classification Pipeline

Three stages. Like grief, but productive.

1. **Rule-based** (instant): Header analysis, domain matching, Gmail categories, subject patterns - classifies ~80% of emails. That's the push broom doing the heavy lifting
2. **ML classifier** (local, fast): Trained on your rule-classified data, classifies the remaining emails in seconds. The smart mop. My pride and joy
3. **Human review**: Interactive terminal UI to approve/reject by category and sender. This is where YOU come in. Try not to break anything

### Training the Classifier

The `train` command creates a classifier using your rule-classified emails as training data. Cross-validates and reports per-class precision, recall, and F1. Can be retrained anytime as you review and reclassify more emails.

## 💾 Data Storage

All local. I don't trust the cloud and neither should you.

Everything is stored in the `gmail-data` Docker volume — the database, classifier model, and auth token. No data leaves your machine during classification.

## 🛡️ Safety Features

I may look reckless, but I'm a professional.

- **Dry-run mode by default** - nothing happens until you say so
- **Move to Trash only** - Gmail retains for 30 days. Plenty of time for regrets
- **Sender/domain whitelist** - protect the important stuff
- **Full audit log per session** - receipts for everything
- **Undo command** - because I knew you'd need it, Newbie

---

## 🔨 Development

See [DEVELOPMENT.md](DEVELOPMENT.md) for build instructions, local development setup, Docker builds, and CI/CD details.
