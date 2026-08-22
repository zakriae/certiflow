# Testing Certiflow by hand

A walkthrough for someone who has just cloned the repo and wants to see it work. It takes about ten
minutes and needs no prior knowledge of the codebase.

## Before you start

Four things must be true.

**1. Docker Desktop is running**, with the three backing containers. If `docker ps` shows nothing:

```bash
docker start certiflow-sql certiflow-azurite certiflow-rabbit
```

If they do not exist at all, create them — see [local-development.md](local-development.md).

**2. You are signed in to Azure.** The extraction step calls a real model, and authentication is
keyless — there is no API key anywhere, so it uses your own `az login` session:

```bash
az login
```

**3. Node 22 is available.** The front-end command below activates it with `nvm use`; the system
Node is never changed.

**4. Sample certificates exist.** They are generated, not real documents:

```bash
cd ~/Documents/GitHub/certiflow && dotnet run --project src/tools/Certiflow.SeedCorpus -- --output /tmp/corpus
```

## Starting it

Two commands, in two terminals. **Both start by changing into the repository** — every path below is
relative to it, and `scripts/run-all.sh` does not exist anywhere else.

**Terminal 1 — the back end** (gateway, six APIs, the worker):

```bash
cd ~/Documents/GitHub/certiflow && bash scripts/run-all.sh
```

It builds once, starts everything, and waits until all eight answer `/health`. It prints
`all eight up` when ready. That single build is deliberate: eight parallel `dotnet run` calls compile
the same shared projects into the same folder and fail on each other's file locks.

**Terminal 2 — the web app:**

```bash
cd ~/Documents/GitHub/certiflow/src/web/certiflow-web
source ~/.nvm/nvm.sh && nvm use && npm start
```

`source ~/.nvm/nvm.sh` is there because **nvm is a shell function, not a program** — it exists only
if your shell profile loads it, and many do not. Without it you get `zsh: command not found: nvm`,
and without `nvm use` you get Angular refusing to run on whatever Node your system has.

Sourcing it affects only that terminal. It is deliberately not added to `~/.zshrc` here: nvm's
default alias is 22, so loading it at profile level would switch **every** shell to Node 22 and
shadow the system Node that other projects may rely on. That is a decision for whoever owns the
machine.

Then open **http://localhost:4200**.

### Starting from scratch

To wipe all data and begin clean:

```bash
cd ~/Documents/GitHub/certiflow && bash scripts/reset-local.sh
```

It drops every schema **and** purges the message broker. Purging matters: dropping the databases
alone leaves the previous run's messages queued, and they redeliver against an empty database.

## The walkthrough

Sign-in prints the demo accounts and the shared password. Each shows you a different system.

### 1. As **Admin** — set up who is being measured

`admin@certiflow.demo`

Go to **Admin**. Two panels.

Press **Register supplier** with the form empty first. You should get four field-level errors at
once, not one — the server validates the whole request and returns every failure together.

Now fill it in and register. Watch what one click does: the supplier appears on the **Dashboard**,
its obligations are built from the published compliance profile, and the **Audit trail** gains three
entries. Those are four separate services reacting to one event.

### 2. As **Supplier** — submit a certificate

`supplier@certiflow.demo`

Notice the navigation is shorter. No review queue, no audit trail — a supplier sees their own
documents and nothing else.

Go to **Upload**, pick your supplier and requirement, and drop in a PDF from
`/tmp/corpus/certificates/`. Match the names if you want a clean result — `meridian-logistics-sarl-iso-9001.pdf`
for Meridian. **Deliberately mismatch them** if you want to see the interesting case.

Upload it. You get `Accepted` and extraction starts.

> Try uploading the **same file twice**. The second returns amber, not green: the system recognises
> the identical file, stores nothing, and runs no second extraction.

### 3. As **Reviewer** — check what the model read

`reviewer@certiflow.demo`

**Review queue**, after ten to twenty seconds. This is the screen worth looking at.

The document is rendered on the left, the extracted fields on the right. Each field shows a
confidence and, underneath, **the exact sentence it was read from**. Click a field and the document
jumps to that page.

The confidence is not the model's opinion — it is never asked. Every field must cite a page and a
verbatim snippet, and that snippet is checked against the document's real text. A citation that
cannot be found scores **zero**, regardless of how plausible the value looks.

If you uploaded a mismatched certificate, `holderName` will sit around **0.80** in amber: the
document says one company, the supplier record says another.

Resolve the mandatory fields, then **Approve**.

**Try Reject instead**, on a different document. You must choose a reason from a list. That reason
is emailed to the supplier and is the only thing telling them what to fix.

### 4. The rule worth testing deliberately

Upload a document **as the reviewer**, then try to approve it yourself.

You are refused: `409 verification.task.segregation_of_duties`. The person who submitted a document
cannot be the person who approves it. Your identity comes from your sign-in token, not from anything
the page sends, so there is no field to change to get around it.

### 5. As **Admin** — the compliance position and the report

Back on the **Dashboard**, your supplier should now read **Compliant**. Click their name.

The supplier page shows each obligation with the evidence behind it: certificate number, issuer, who
it was issued to, when it expires, and **who approved it and when**.

Press **Generate report**. A few seconds later a PDF is available — the certificate a buyer forwards
to an auditor. It carries a verification hash computed over the *facts*, not the file: restyle the
PDF and the hash is unchanged, alter a certificate number and it is not.

### 6. **Notifications** — mail that was never sent

Every message is held, not sent, and marked *held — not sent* in amber. Outbound email is disabled
by default and turning it on takes a deliberate config change: a public demo that can mail arbitrary
addresses is an abuse vector.

Open one. It reads like something a person would send.

### 7. **Audit trail** — the part that is hard to fake

Every event, hash-chained: each entry's hash covers the one before it.

Press **Verify chain**. You get a green confirmation and a count.

Now break it. This edits one row directly in SQL, exactly as someone with database access would:

```bash
TOKEN=$(curl -s -X POST http://localhost:5000/auth/token -H "Content-Type: application/json" \
  -d '{"email":"admin@certiflow.demo","password":"Certiflow!Demo1"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")
curl -s -X POST http://localhost:5310/api/audit/_tamper -H "Authorization: Bearer $TOKEN"
```

Press **Verify chain** again. It reports the break, names the entry, and prints the stored hash
against the recomputed one. Press **Show entry N** to jump to the altered row.

The row itself looks completely ordinary. That is the point: the tampering is invisible in the data
and provable by the chain.

### 8. As **Auditor** — read everything, change nothing

`auditor@certiflow.demo`

No Upload, no Review queue. Try `http://localhost:4200/review` directly and you are redirected. The
server refuses independently — the hidden link is a convenience, not the control.

An auditor can still generate a compliance report, which is the job the role exists for.

## Stopping

```bash
pkill -f "Certiflow\."
```

Ctrl-C in the front-end terminal. Leave the Docker containers running, or stop them with
`docker stop certiflow-sql certiflow-azurite certiflow-rabbit`.

## If something does not work

**The review queue stays empty.** Extraction calls Azure OpenAI. Check `az login` is still valid, and
look at `/tmp/certiflow-worker.log`.

**Everything returns 401.** The gateway generates its signing key in memory, so restarting it
invalidates every token. Sign in again.

**A service will not start.** Look at `/tmp/certiflow-<name>.log`. The commonest cause is a container
that is not running.

**`scripts/run-all.sh: No such file or directory`.** You are not in the repository. Every command
here assumes `~/Documents/GitHub/certiflow`, not the folder above it.

**`command not found: nvm`.** nvm is a shell function your profile has not loaded. Run
`source ~/.nvm/nvm.sh` first, in that same terminal.

**`The Angular CLI requires a minimum Node.js version of v22`.** nvm loaded but `nvm use` was not
run, so the shell is still on the system Node.

**The document preview is blank.** Azurite needs a CORS rule for the browser to fetch PDFs directly:

```bash
az storage cors add --services b --methods GET HEAD OPTIONS --origins "http://localhost:4200" \
  --allowed-headers "*" --exposed-headers "*" --max-age 3600 --connection-string "UseDevelopmentStorage=true"
```
