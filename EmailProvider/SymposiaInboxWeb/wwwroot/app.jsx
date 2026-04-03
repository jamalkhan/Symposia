const { useEffect, useState } = React;

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    },
    ...options
  });

  if (response.status === 204) {
    return null;
  }

  const isJson = response.headers.get("content-type")?.includes("application/json");
  const body = isJson ? await response.json() : await response.text();
  if (!response.ok) {
    const message = typeof body === "object" && body?.error ? body.error : `Request failed (${response.status})`;
    throw new Error(message);
  }

  return body;
}

function formatDate(value) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

function App() {
  const [mode, setMode] = useState("login");
  const [domains, setDomains] = useState([]);
  const [account, setAccount] = useState(null);
  const [contacts, setContacts] = useState([]);
  const [counts, setCounts] = useState({ inbox: 0, sent: 0, trash: 0 });
  const [folder, setFolder] = useState("inbox");
  const [messages, setMessages] = useState([]);
  const [selectedMessage, setSelectedMessage] = useState(null);
  const [query, setQuery] = useState("");
  const [composeOpen, setComposeOpen] = useState(false);
  const [formState, setFormState] = useState({
    username: "",
    domain: "",
    displayName: "",
    emailAddress: "",
    password: ""
  });
  const [contactDraft, setContactDraft] = useState({
    displayName: "",
    emailAddress: ""
  });
  const [composeDraft, setComposeDraft] = useState({
    to: "",
    cc: "",
    bcc: "",
    subject: "",
    plainTextBody: "",
    htmlBody: "",
    replyToMessageId: ""
  });
  const [error, setError] = useState("");
  const [success, setSuccess] = useState("");

  useEffect(() => {
    bootstrap();
  }, []);

  async function bootstrap() {
    try {
      const hostedDomains = await api("/api/auth/domains");
      setDomains(hostedDomains);
      setFormState((current) => ({ ...current, domain: current.domain || hostedDomains[0] || "" }));
    } catch (err) {
      setError(err.message);
    }

    try {
      const me = await api("/api/auth/me");
      if (me) {
        await hydrate(me);
      }
    } catch {
      // anonymous session
    }
  }

  async function hydrate(currentAccount) {
    setAccount(currentAccount);
    const bootstrapPayload = await api("/api/mailbox/bootstrap");
    setContacts(bootstrapPayload.contacts);
    setCounts(bootstrapPayload.counts);
    setMessages(bootstrapPayload.recentMessages);
    if (bootstrapPayload.recentMessages[0]) {
      await openMessage(bootstrapPayload.recentMessages[0].messageId);
    } else {
      setSelectedMessage(null);
    }
  }

  async function login(event) {
    event.preventDefault();
    setError("");
    try {
      const me = await api("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({
          emailAddress: formState.emailAddress,
          password: formState.password
        })
      });
      await hydrate(me);
    } catch (err) {
      setError(err.message);
    }
  }

  async function register(event) {
    event.preventDefault();
    setError("");
    try {
      const me = await api("/api/auth/register", {
        method: "POST",
        body: JSON.stringify({
          username: formState.username,
          domain: formState.domain,
          password: formState.password,
          displayName: formState.displayName
        })
      });
      setSuccess(`Inbox ${me.address} is ready.`);
      await hydrate(me);
    } catch (err) {
      setError(err.message);
    }
  }

  async function logout() {
    await api("/api/auth/logout", { method: "POST" });
    setAccount(null);
    setContacts([]);
    setCounts({ inbox: 0, sent: 0, trash: 0 });
    setMessages([]);
    setSelectedMessage(null);
  }

  async function refreshMessages(nextFolder = folder, nextQuery = query) {
    const params = new URLSearchParams({ folder: nextFolder });
    if (nextQuery) {
      params.set("q", nextQuery);
    }

    const items = await api(`/api/mailbox/messages?${params.toString()}`);
    setMessages(items);
    const bootstrapPayload = await api("/api/mailbox/bootstrap");
    setCounts(bootstrapPayload.counts);
    setContacts(bootstrapPayload.contacts);

    if (items.length === 0) {
      setSelectedMessage(null);
    } else if (!items.some((item) => item.messageId === selectedMessage?.messageId)) {
      await openMessage(items[0].messageId);
    }
  }

  async function openMessage(messageId) {
    const detail = await api(`/api/mailbox/messages/${messageId}`);
    setSelectedMessage(detail);
  }

  async function markRead(isRead) {
    if (!selectedMessage) {
      return;
    }

    await api(`/api/mailbox/messages/${selectedMessage.messageId}/${isRead ? "read" : "unread"}`, {
      method: "POST"
    });
    await refreshMessages(folder, query);
    await openMessage(selectedMessage.messageId);
  }

  async function moveTo(folderName) {
    if (!selectedMessage) {
      return;
    }

    await api(`/api/mailbox/messages/${selectedMessage.messageId}/${folderName === "trash" ? "delete" : "restore"}`, {
      method: "POST"
    });
    await refreshMessages(folder, query);
  }

  async function sendCompose(event) {
    event.preventDefault();
    setError("");
    try {
      const result = await api("/api/mailbox/compose", {
        method: "POST",
        body: JSON.stringify(composeDraft)
      });
      setComposeOpen(false);
      setComposeDraft({
        to: "",
        cc: "",
        bcc: "",
        subject: "",
        plainTextBody: "",
        htmlBody: "",
        replyToMessageId: ""
      });
      setFolder("sent");
      setSuccess(`Message sent. ${result.deliveredLocalCount} local deliveries, ${result.queuedExternalCount} queued externally.`);
      await refreshMessages("sent", "");
    } catch (err) {
      setError(err.message);
    }
  }

  async function saveContact(event) {
    event.preventDefault();
    setError("");
    try {
      await api("/api/contacts", {
        method: "POST",
        body: JSON.stringify(contactDraft)
      });
      setContactDraft({ displayName: "", emailAddress: "" });
      setContacts(await api("/api/contacts"));
    } catch (err) {
      setError(err.message);
    }
  }

  if (!account) {
    return (
      <div className="shell">
        <div className="auth-shell">
          <section className="hero-card">
            <div className="hero-kicker">Symposia Mail</div>
            <h1 className="hero-title">A Gmail-style inbox tuned to #7800F0.</h1>
            <p className="hero-copy">
              Hosted inbox creation, mailbox search, read state, contacts, compose, reply, sent mail, and a clean violet workspace.
            </p>
            <div className="hero-stats">
              <div className="hero-stat">
                <strong>{domains.length}</strong>
                <span>Hosted domains available right now</span>
              </div>
              <div className="hero-stat">
                <strong>REST</strong>
                <span>Backed by .NET Web API on its own port</span>
              </div>
              <div className="hero-stat">
                <strong>React</strong>
                <span>Single-page inbox experience</span>
              </div>
            </div>
          </section>
          <section className="card">
            <div className="tabs">
              <button className={`tab ${mode === "login" ? "active" : ""}`} onClick={() => setMode("login")}>Log In</button>
              <button className={`tab ${mode === "register" ? "active" : ""}`} onClick={() => setMode("register")}>Create Inbox</button>
            </div>

            {mode === "login" ? (
              <form className="field-grid" onSubmit={login}>
                <div className="field">
                  <label>Email address</label>
                  <input value={formState.emailAddress} onChange={(event) => setFormState({ ...formState, emailAddress: event.target.value })} />
                </div>
                <div className="field">
                  <label>Password</label>
                  <input type="password" value={formState.password} onChange={(event) => setFormState({ ...formState, password: event.target.value })} />
                </div>
                <div className="action-row">
                  <button className="button primary" type="submit">Open Inbox</button>
                </div>
              </form>
            ) : (
              <form className="field-grid" onSubmit={register}>
                <div className="field-grid two">
                  <div className="field">
                    <label>Username</label>
                    <input value={formState.username} onChange={(event) => setFormState({ ...formState, username: event.target.value })} />
                  </div>
                  <div className="field">
                    <label>Hosted domain</label>
                    <select value={formState.domain} onChange={(event) => setFormState({ ...formState, domain: event.target.value })}>
                      {domains.map((domain) => <option key={domain} value={domain}>{domain}</option>)}
                    </select>
                  </div>
                </div>
                <div className="field">
                  <label>Display name</label>
                  <input value={formState.displayName} onChange={(event) => setFormState({ ...formState, displayName: event.target.value })} />
                </div>
                <div className="field">
                  <label>Password</label>
                  <input type="password" value={formState.password} onChange={(event) => setFormState({ ...formState, password: event.target.value })} />
                </div>
                <div className="action-row">
                  <button className="button primary" type="submit">Create Inbox</button>
                </div>
              </form>
            )}

            {error && <div className="error-banner">{error}</div>}
            {success && <div className="success-banner">{success}</div>}
          </section>
        </div>
      </div>
    );
  }

  return (
    <div className="shell">
      <div className="app-shell">
        <aside className="hero-card sidebar">
          <div>
            <div className="brand">
              <div className="brand-mark">S</div>
              <div>
                <strong>Symposia Mail</strong>
                <div className="muted">Inbox experience</div>
              </div>
            </div>
            <div className="account-chip">
              <div className="mini-label">{account.displayName}</div>
              <h3>{account.address}</h3>
              <div className="muted">Mailbox {account.mailboxId}</div>
            </div>
            <div className="action-row">
              <button className="button primary" onClick={() => setComposeOpen(true)}>Compose</button>
              <button className="button ghost" onClick={logout}>Log Out</button>
            </div>
            <div className="folder-nav">
              {[
                { id: "inbox", label: "Inbox", count: counts.inbox },
                { id: "sent", label: "Sent", count: counts.sent },
                { id: "trash", label: "Trash", count: counts.trash }
              ].map((item) => (
                <button key={item.id} className={`folder-button ${folder === item.id ? "active" : ""}`} onClick={async () => {
                  setFolder(item.id);
                  await refreshMessages(item.id, query);
                }}>
                  <span>{item.label}</span>
                  <strong>{item.count}</strong>
                </button>
              ))}
            </div>
          </div>

          <div className="contacts">
            <h3>Address Book</h3>
            <form className="field-grid" onSubmit={saveContact}>
              <div className="field">
                <label>Name</label>
                <input value={contactDraft.displayName} onChange={(event) => setContactDraft({ ...contactDraft, displayName: event.target.value })} />
              </div>
              <div className="field">
                <label>Email</label>
                <input value={contactDraft.emailAddress} onChange={(event) => setContactDraft({ ...contactDraft, emailAddress: event.target.value })} />
              </div>
              <button className="button secondary" type="submit">Save Contact</button>
            </form>
            <div>
              {contacts.map((contact) => (
                <div className="contact-item" key={contact.contactId}>
                  <strong>{contact.displayName}</strong>
                  <span className="muted">{contact.emailAddress}</span>
                </div>
              ))}
            </div>
          </div>
        </aside>

        <section className="card message-list">
          <div className="search-wrap">
            <input
              className="search-box"
              placeholder="Search your mail"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              onKeyDown={async (event) => {
                if (event.key === "Enter") {
                  await refreshMessages(folder, event.target.value);
                }
              }}
            />
          </div>
          <div className="message-list-body">
            {messages.length === 0 ? (
              <div className="muted">No mail in this folder yet.</div>
            ) : messages.map((message) => (
              <button className={`message-item ${message.isRead ? "" : "unread"} ${selectedMessage?.messageId === message.messageId ? "active" : ""}`} key={message.messageId} onClick={() => openMessage(message.messageId)}>
                <div className="message-meta">
                  <span className="message-from">{message.displayFrom}</span>
                  <span className="muted">{formatDate(message.receivedAtUtc)}</span>
                </div>
                <div className="message-subject">{message.subject || "(no subject)"}</div>
                <div className="message-preview">{message.preview}</div>
              </button>
            ))}
          </div>
        </section>

        <section className="card message-detail">
          {!selectedMessage ? (
            <div className="muted">Select a message to start reading.</div>
          ) : (
            <>
              <div className="detail-header">
                <div>
                  <div className="mini-label">{selectedMessage.folder}</div>
                  <h2>{selectedMessage.subject || "(no subject)"}</h2>
                  <div className="muted">From {selectedMessage.headerFrom || selectedMessage.envelopeFrom}</div>
                  <div className="muted">To {selectedMessage.headerTo || selectedMessage.deliveredAddresses.join(", ")}</div>
                  <div className="muted">{formatDate(selectedMessage.receivedAtUtc)}</div>
                </div>
                <div className="detail-actions">
                  <button className="button secondary" onClick={() => {
                    setComposeDraft({
                      to: selectedMessage.envelopeFrom,
                      cc: "",
                      bcc: "",
                      subject: selectedMessage.subject?.startsWith("Re:") ? selectedMessage.subject : `Re: ${selectedMessage.subject || ""}`,
                      plainTextBody: `\n\n--- Original message ---\n${selectedMessage.plainTextBody || ""}`,
                      htmlBody: "",
                      replyToMessageId: selectedMessage.messageId
                    });
                    setComposeOpen(true);
                  }}>Reply</button>
                  <button className="button ghost" onClick={() => markRead(!selectedMessage.isRead)}>
                    Mark as {selectedMessage.isRead ? "Unread" : "Read"}
                  </button>
                  {selectedMessage.folder === "trash" ? (
                    <button className="button ghost" onClick={() => moveTo("inbox")}>Restore</button>
                  ) : (
                    <button className="button danger" onClick={() => moveTo("trash")}>Delete</button>
                  )}
                </div>
              </div>
              <div className="detail-body">
                <pre style={{ whiteSpace: "pre-wrap", margin: 0 }}>{selectedMessage.plainTextBody || "(No plain text body)"}</pre>
                {selectedMessage.htmlBody && (
                  <div className="detail-html" dangerouslySetInnerHTML={{ __html: selectedMessage.htmlBody }} />
                )}
              </div>
            </>
          )}
        </section>
      </div>

      {composeOpen && (
        <div className="compose-overlay">
          <div className="card compose-modal">
            <div className="detail-header">
              <div>
                <div className="mini-label">Compose</div>
                <h2>New message</h2>
              </div>
              <button className="button ghost" onClick={() => setComposeOpen(false)}>Close</button>
            </div>
            <form className="stack" onSubmit={sendCompose}>
              <div className="field">
                <label>To</label>
                <input value={composeDraft.to} onChange={(event) => setComposeDraft({ ...composeDraft, to: event.target.value })} />
              </div>
              <div className="field-grid two">
                <div className="field">
                  <label>Cc</label>
                  <input value={composeDraft.cc} onChange={(event) => setComposeDraft({ ...composeDraft, cc: event.target.value })} />
                </div>
                <div className="field">
                  <label>Bcc</label>
                  <input value={composeDraft.bcc} onChange={(event) => setComposeDraft({ ...composeDraft, bcc: event.target.value })} />
                </div>
              </div>
              <div className="field">
                <label>Subject</label>
                <input value={composeDraft.subject} onChange={(event) => setComposeDraft({ ...composeDraft, subject: event.target.value })} />
              </div>
              <div className="field">
                <label>Plain text body</label>
                <textarea value={composeDraft.plainTextBody} onChange={(event) => setComposeDraft({ ...composeDraft, plainTextBody: event.target.value })} />
              </div>
              <div className="field">
                <label>Optional HTML body</label>
                <textarea value={composeDraft.htmlBody} onChange={(event) => setComposeDraft({ ...composeDraft, htmlBody: event.target.value })} />
              </div>
              <div className="action-row">
                <button className="button primary" type="submit">Send</button>
                <button className="button ghost" type="button" onClick={() => setComposeOpen(false)}>Cancel</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {(error || success) && (
        <div style={{ position: "fixed", right: 24, bottom: 24, width: 320 }}>
          {error && <div className="error-banner">{error}</div>}
          {success && <div className="success-banner">{success}</div>}
        </div>
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
