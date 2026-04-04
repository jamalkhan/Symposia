(function () {
  const h = React.createElement;
  const { useEffect, useMemo, useState } = React;

  const emptyCounts = { inbox: 0, sent: 0, trash: 0 };
  const emptyComposeDraft = {
    to: "",
    cc: "",
    bcc: "",
    subject: "",
    plainTextBody: "",
    htmlBody: "",
    replyToMessageId: ""
  };

  function cx() {
    return Array.from(arguments).filter(Boolean).join(" ");
  }

  function fragment(children, key) {
    return h(React.Fragment, { key }, children);
  }

  function icon(symbol) {
    return h("span", { className: "symbol" }, symbol);
  }

  async function api(path, options, csrfToken) {
    const headers = Object.assign(
      {},
      options && options.body ? { "Content-Type": "application/json" } : null,
      csrfToken && options && options.method && options.method !== "GET" ? { "X-Symposia-Csrf": csrfToken } : null,
      options && options.headers ? options.headers : null);

    const response = await fetch(path, Object.assign({
      credentials: "include",
      headers: headers
    }, options || {}));

    if (response.status === 204) {
      return null;
    }

    const contentType = response.headers.get("content-type") || "";
    const body = contentType.indexOf("application/json") >= 0
      ? await response.json()
      : await response.text();

    if (!response.ok) {
      const message = typeof body === "object" && body && body.error
        ? body.error
        : "Request failed (" + response.status + ")";
      throw new Error(message);
    }

    return body;
  }

  function parseLabels(value) {
    return value
      .split(",")
      .map(function (item) { return item.trim().toLowerCase(); })
      .filter(Boolean)
      .filter(function (item, index, items) { return items.indexOf(item) === index; });
  }

  function formatDate(value) {
    return new Date(value).toLocaleString([], {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit"
    });
  }

  function AuthScreen(props) {
    const mode = props.mode;
    const domains = props.domains;
    const formState = props.formState;
    const setFormState = props.setFormState;

    return h("div", { className: "shell" }, [
      h("div", { className: "auth-shell", key: "auth-shell" }, [
        h("section", { className: "hero-card", key: "hero" }, [
          h("div", { className: "hero-kicker", key: "kicker" }, "Symposia Mail"),
          h("h1", { className: "hero-title", key: "title" }, "A violet inbox that finally behaves like your mail app."),
          h("p", { className: "hero-copy", key: "copy" }, "Create hosted inboxes on approved domains, search threads, manage contacts, compose, reply, star, label, and actually own the experience."),
          h("div", { className: "hero-stats", key: "stats" }, [
            h("div", { className: "hero-stat", key: "domains" }, [
              h("strong", null, String(domains.length)),
              h("span", null, "Hosted domains ready for account creation")
            ]),
            h("div", { className: "hero-stat", key: "rest" }, [
              h("strong", null, "REST"),
              h("span", null, "Inbox, threads, contacts, compose, auth")
            ]),
            h("div", { className: "hero-stat", key: "relay" }, [
              h("strong", null, "Relay"),
              h("span", null, "Local delivery now, external delivery when configured")
            ])
          ])
        ]),
        h("section", { className: "card", key: "form" }, [
          h("div", { className: "tabs", key: "tabs" }, [
            h("button", {
              className: cx("tab", mode === "login" && "active"),
              onClick: function () { props.setMode("login"); },
              type: "button",
              key: "login"
            }, "Log In"),
            h("button", {
              className: cx("tab", mode === "register" && "active"),
              onClick: function () { props.setMode("register"); },
              type: "button",
              key: "register"
            }, "Create Inbox")
          ]),
          mode === "login"
            ? h("form", { className: "field-grid", onSubmit: props.login, key: "login-form" }, [
                field("Email address", h("input", {
                  value: formState.emailAddress,
                  onChange: function (event) { setFormState(Object.assign({}, formState, { emailAddress: event.target.value })); }
                }), "login-email"),
                field("Password", h("input", {
                  type: "password",
                  value: formState.password,
                  onChange: function (event) { setFormState(Object.assign({}, formState, { password: event.target.value })); }
                }), "login-password"),
                h("div", { className: "action-row", key: "actions" }, [
                  h("button", { className: "button primary", type: "submit" }, "Open Inbox")
                ])
              ])
            : h("form", { className: "field-grid", onSubmit: props.register, key: "register-form" }, [
                h("div", { className: "field-grid two", key: "row-1" }, [
                  field("Username", h("input", {
                    value: formState.username,
                    onChange: function (event) { setFormState(Object.assign({}, formState, { username: event.target.value })); }
                  }), "register-username"),
                  field("Hosted domain", h("select", {
                    value: formState.domain,
                    onChange: function (event) { setFormState(Object.assign({}, formState, { domain: event.target.value })); }
                  }, domains.map(function (domain) {
                    return h("option", { key: domain, value: domain }, domain);
                  })), "register-domain")
                ]),
                field("Display name", h("input", {
                  value: formState.displayName,
                  onChange: function (event) { setFormState(Object.assign({}, formState, { displayName: event.target.value })); }
                }), "register-display"),
                field("Password", h("input", {
                  type: "password",
                  value: formState.password,
                  onChange: function (event) { setFormState(Object.assign({}, formState, { password: event.target.value })); }
                }), "register-password"),
                h("div", { className: "action-row", key: "actions" }, [
                  h("button", { className: "button primary", type: "submit" }, "Create Inbox")
                ])
              ]),
          props.error ? h("div", { className: "error-banner", key: "error" }, props.error) : null,
          props.success ? h("div", { className: "success-banner", key: "success" }, props.success) : null
        ])
      ])
    ]);
  }

  function field(label, inputElement, key) {
    return h("div", { className: "field", key: key }, [
      h("label", null, label),
      inputElement
    ]);
  }

  function App() {
    const [mode, setMode] = useState("login");
    const [domains, setDomains] = useState([]);
    const [account, setAccount] = useState(null);
    const [csrfToken, setCsrfToken] = useState("");
    const [contacts, setContacts] = useState([]);
    const [counts, setCounts] = useState(emptyCounts);
    const [folder, setFolder] = useState("inbox");
    const [query, setQuery] = useState("");
    const [page, setPage] = useState(1);
    const [threadPage, setThreadPage] = useState({ page: 1, pageSize: 25, totalCount: 0, totalPages: 1, items: [] });
    const [selectedThreadId, setSelectedThreadId] = useState(null);
    const [selectedThread, setSelectedThread] = useState(null);
    const [selectedMessageId, setSelectedMessageId] = useState(null);
    const [composeOpen, setComposeOpen] = useState(false);
    const [composeDraft, setComposeDraft] = useState(emptyComposeDraft);
    const [contactDraft, setContactDraft] = useState({ displayName: "", emailAddress: "" });
    const [labelDraft, setLabelDraft] = useState("");
    const [formState, setFormState] = useState({
      username: "",
      domain: "",
      displayName: "",
      emailAddress: "",
      password: ""
    });
    const [error, setError] = useState("");
    const [success, setSuccess] = useState("");

    const selectedMessage = useMemo(function () {
      if (!selectedThread || !selectedThread.messages || selectedThread.messages.length === 0) {
        return null;
      }

      if (!selectedMessageId) {
        return selectedThread.messages[selectedThread.messages.length - 1];
      }

      return selectedThread.messages.find(function (message) {
        return message.messageId === selectedMessageId;
      }) || selectedThread.messages[selectedThread.messages.length - 1];
    }, [selectedThread, selectedMessageId]);

    useEffect(function () {
      bootstrap();
    }, []);

    async function apiCall(path, options) {
      const body = await api(path, options, csrfToken);
      if (body && body.csrfToken) {
        setCsrfToken(body.csrfToken);
      } else if (body && body.account && body.account.csrfToken) {
        setCsrfToken(body.account.csrfToken);
      }

      return body;
    }

    async function bootstrap() {
      try {
        const hostedDomains = await api("/api/auth/domains", null, null);
        setDomains(hostedDomains);
        setFormState(function (current) {
          return Object.assign({}, current, { domain: current.domain || hostedDomains[0] || "" });
        });
      } catch (err) {
        setError(err.message);
      }

      try {
        const me = await api("/api/auth/me", null, null);
        if (me) {
          await hydrate(me, "inbox", "", 1);
        }
      } catch (err) {
        if (err && err.message && err.message.indexOf("401") >= 0) {
          return;
        }
      }
    }

    async function hydrate(session, nextFolder, nextQuery, nextPage) {
      setAccount(session);
      setCsrfToken(session.csrfToken || "");

      const bootstrapPayload = await api("/api/mailbox/bootstrap", null, session.csrfToken || "");
      setAccount(bootstrapPayload.account);
      setCsrfToken(bootstrapPayload.account.csrfToken || "");
      setContacts(bootstrapPayload.contacts || []);
      setCounts(bootstrapPayload.counts || emptyCounts);
      await loadThreadPage(nextFolder || folder, nextQuery || "", nextPage || 1, null, bootstrapPayload.account.csrfToken || session.csrfToken || "");
    }

    async function loadThreadPage(nextFolder, nextQuery, nextPage, preferredThreadId, overrideCsrfToken) {
      const params = new URLSearchParams({
        folder: nextFolder || "inbox",
        page: String(nextPage || 1),
        pageSize: "25"
      });

      if (nextQuery) {
        params.set("q", nextQuery);
      }

      const payload = await api("/api/mailbox/threads?" + params.toString(), null, overrideCsrfToken || csrfToken);
      setFolder(nextFolder);
      setQuery(nextQuery);
      setPage(nextPage);
      setThreadPage(payload);

      const nextThreadId = preferredThreadId && payload.items.some(function (item) { return item.threadId === preferredThreadId; })
        ? preferredThreadId
        : (payload.items[0] ? payload.items[0].threadId : null);

      if (nextThreadId) {
        await openThread(nextThreadId, overrideCsrfToken || csrfToken);
      } else {
        setSelectedThreadId(null);
        setSelectedThread(null);
        setSelectedMessageId(null);
      }
    }

    async function openThread(threadId, overrideCsrfToken) {
      const payload = await api("/api/mailbox/threads/" + encodeURIComponent(threadId), null, overrideCsrfToken || csrfToken);
      setSelectedThreadId(threadId);
      setSelectedThread(payload);
      const newestMessage = payload.messages && payload.messages.length > 0
        ? payload.messages[payload.messages.length - 1]
        : null;
      setSelectedMessageId(newestMessage ? newestMessage.messageId : null);
      setLabelDraft(newestMessage && newestMessage.labels ? newestMessage.labels.join(", ") : "");
    }

    async function refreshCurrentView(preferredThreadId) {
      const bootstrapPayload = await apiCall("/api/mailbox/bootstrap");
      setCounts(bootstrapPayload.counts || emptyCounts);
      setContacts(bootstrapPayload.contacts || []);
      await loadThreadPage(folder, query, page, preferredThreadId || selectedThreadId);
    }

    async function login(event) {
      event.preventDefault();
      setError("");
      setSuccess("");

      try {
        const session = await api("/api/auth/login", {
          method: "POST",
          body: JSON.stringify({
            emailAddress: formState.emailAddress,
            password: formState.password
          })
        }, null);
        await hydrate(session, "inbox", "", 1);
      } catch (err) {
        setError(err.message);
      }
    }

    async function register(event) {
      event.preventDefault();
      setError("");
      setSuccess("");

      try {
        const session = await api("/api/auth/register", {
          method: "POST",
          body: JSON.stringify({
            username: formState.username,
            domain: formState.domain,
            displayName: formState.displayName,
            password: formState.password
          })
        }, null);
        setSuccess("Inbox " + session.address + " is ready.");
        await hydrate(session, "inbox", "", 1);
      } catch (err) {
        setError(err.message);
      }
    }

    async function logout() {
      await apiCall("/api/auth/logout", { method: "POST" });
      setAccount(null);
      setCsrfToken("");
      setContacts([]);
      setCounts(emptyCounts);
      setFolder("inbox");
      setQuery("");
      setPage(1);
      setThreadPage({ page: 1, pageSize: 25, totalCount: 0, totalPages: 1, items: [] });
      setSelectedThreadId(null);
      setSelectedThread(null);
      setSelectedMessageId(null);
      setComposeOpen(false);
      setSuccess("");
    }

    async function submitSearch(event) {
      event.preventDefault();
      await loadThreadPage(folder, query, 1, null);
    }

    async function movePage(direction) {
      const targetPage = Math.max(1, Math.min(threadPage.totalPages || 1, page + direction));
      if (targetPage !== page) {
        await loadThreadPage(folder, query, targetPage, selectedThreadId);
      }
    }

    async function toggleRead(isRead) {
      if (!selectedMessage) {
        return;
      }

      await apiCall("/api/mailbox/messages/" + encodeURIComponent(selectedMessage.messageId) + "/" + (isRead ? "read" : "unread"), {
        method: "POST"
      });
      await refreshCurrentView(selectedThreadId);
    }

    async function moveToFolder(targetFolder) {
      if (!selectedMessage) {
        return;
      }

      const action = targetFolder === "trash" ? "delete" : "restore";
      await apiCall("/api/mailbox/messages/" + encodeURIComponent(selectedMessage.messageId) + "/" + action, {
        method: "POST"
      });
      await refreshCurrentView(selectedThreadId);
    }

    async function toggleStar() {
      if (!selectedMessage) {
        return;
      }

      await apiCall("/api/mailbox/messages/" + encodeURIComponent(selectedMessage.messageId) + "/star", {
        method: "POST",
        body: JSON.stringify({ isStarred: !selectedMessage.isStarred })
      });
      await refreshCurrentView(selectedThreadId);
    }

    async function saveLabels() {
      if (!selectedMessage) {
        return;
      }

      await apiCall("/api/mailbox/messages/" + encodeURIComponent(selectedMessage.messageId) + "/labels", {
        method: "POST",
        body: JSON.stringify({ labels: parseLabels(labelDraft) })
      });
      await refreshCurrentView(selectedThreadId);
    }

    async function sendCompose(event) {
      event.preventDefault();
      setError("");
      setSuccess("");

      try {
        const result = await apiCall("/api/mailbox/compose", {
          method: "POST",
          body: JSON.stringify(composeDraft)
        });
        setComposeOpen(false);
        setComposeDraft(emptyComposeDraft);
        setSuccess("Message sent. " + result.deliveredLocalCount + " local deliveries, " + result.queuedExternalCount + " queued externally.");
        await loadThreadPage("sent", "", 1, null);
      } catch (err) {
        setError(err.message);
      }
    }

    async function saveContact(event) {
      event.preventDefault();
      setError("");

      try {
        await apiCall("/api/contacts", {
          method: "POST",
          body: JSON.stringify(contactDraft)
        });
        setContactDraft({ displayName: "", emailAddress: "" });
        setContacts(await apiCall("/api/contacts"));
      } catch (err) {
        setError(err.message);
      }
    }

    async function deleteContact(contactId) {
      await apiCall("/api/contacts/" + encodeURIComponent(contactId), { method: "DELETE" });
      setContacts(await apiCall("/api/contacts"));
    }

    function openReply() {
      if (!selectedMessage) {
        return;
      }

      setComposeDraft({
        to: selectedMessage.envelopeFrom,
        cc: "",
        bcc: "",
        subject: selectedMessage.subject && selectedMessage.subject.toLowerCase().indexOf("re:") === 0
          ? selectedMessage.subject
          : "Re: " + (selectedMessage.subject || "(no subject)"),
        plainTextBody: "\n\n--- Original message ---\n" + (selectedMessage.plainTextBody || ""),
        htmlBody: "",
        replyToMessageId: selectedMessage.messageId
      });
      setComposeOpen(true);
    }

    if (!account) {
      return h(AuthScreen, {
        mode: mode,
        setMode: setMode,
        domains: domains,
        formState: formState,
        setFormState: setFormState,
        login: login,
        register: register,
        error: error,
        success: success
      });
    }

    const selectedThreadMessages = selectedThread && selectedThread.messages ? selectedThread.messages : [];

    return h("div", { className: "shell" }, [
      h("div", { className: "app-shell", key: "app-shell" }, [
        h("aside", { className: "hero-card sidebar", key: "sidebar" }, [
          h("div", { key: "sidebar-top" }, [
            h("div", { className: "brand", key: "brand" }, [
              h("div", { className: "brand-mark" }, "S"),
              h("div", null, [
                h("strong", null, "Symposia Mail"),
                h("div", { className: "muted" }, "Inbox workspace")
              ])
            ]),
            h("div", { className: "account-chip", key: "chip" }, [
              h("div", { className: "mini-label" }, account.displayName),
              h("h3", null, account.address),
              h("div", { className: "muted" }, "Mailbox " + account.mailboxId)
            ]),
            h("div", { className: "action-row", key: "actions" }, [
              h("button", {
                className: "button primary",
                type: "button",
                onClick: function () { setComposeDraft(emptyComposeDraft); setComposeOpen(true); }
              }, "Compose"),
              h("button", {
                className: "button ghost",
                type: "button",
                onClick: logout
              }, "Log Out")
            ]),
            h("div", { className: "folder-nav", key: "folders" }, [
              { id: "inbox", label: "Inbox", count: counts.inbox },
              { id: "sent", label: "Sent", count: counts.sent },
              { id: "trash", label: "Trash", count: counts.trash }
            ].map(function (item) {
              return h("button", {
                key: item.id,
                className: cx("folder-button", folder === item.id && "active"),
                type: "button",
                onClick: function () { loadThreadPage(item.id, "", 1, null); }
              }, [
                h("span", null, item.label),
                h("strong", null, String(item.count))
              ]);
            }))
          ]),
          h("div", { className: "contacts", key: "contacts" }, [
            h("h3", null, "Address Book"),
            h("form", { className: "field-grid", onSubmit: saveContact }, [
              field("Name", h("input", {
                value: contactDraft.displayName,
                onChange: function (event) {
                  setContactDraft(Object.assign({}, contactDraft, { displayName: event.target.value }));
                }
              }), "contact-name"),
              field("Email", h("input", {
                value: contactDraft.emailAddress,
                onChange: function (event) {
                  setContactDraft(Object.assign({}, contactDraft, { emailAddress: event.target.value }));
                }
              }), "contact-email"),
              h("button", { className: "button secondary", type: "submit" }, "Save Contact")
            ]),
            h("div", { className: "contact-list" }, contacts.map(function (contact) {
              return h("div", { className: "contact-item contact-row", key: contact.contactId }, [
                h("div", { className: "contact-copy" }, [
                  h("strong", null, contact.displayName),
                  h("span", { className: "muted" }, contact.emailAddress)
                ]),
                h("button", {
                  className: "button ghost slim",
                  type: "button",
                  onClick: function () { deleteContact(contact.contactId); }
                }, "Remove")
              ]);
            }))
          ])
        ]),
        h("section", { className: "card message-list", key: "thread-list" }, [
          h("form", { className: "search-wrap", onSubmit: submitSearch, key: "search" }, [
            h("div", { className: "search-row" }, [
              h("input", {
                className: "search-box",
                placeholder: "Search subject, sender, labels, or content",
                value: query,
                onChange: function (event) { setQuery(event.target.value); }
              }),
              h("button", { className: "button secondary", type: "submit" }, "Search")
            ])
          ]),
          h("div", { className: "thread-toolbar", key: "toolbar" }, [
            h("div", { className: "mini-label" }, folder),
            h("div", { className: "pagination" }, [
              h("button", {
                className: "button ghost slim",
                type: "button",
                disabled: page <= 1,
                onClick: function () { movePage(-1); }
              }, "Previous"),
              h("span", { className: "muted" }, "Page " + threadPage.page + " of " + threadPage.totalPages),
              h("button", {
                className: "button ghost slim",
                type: "button",
                disabled: page >= threadPage.totalPages,
                onClick: function () { movePage(1); }
              }, "Next")
            ])
          ]),
          h("div", { className: "message-list-body", key: "body" }, threadPage.items.length === 0
            ? h("div", { className: "empty-state" }, "No conversations in this folder yet.")
            : threadPage.items.map(function (thread) {
                return h("button", {
                  key: thread.threadId,
                  className: cx("message-item", thread.unreadCount > 0 && "unread", selectedThreadId === thread.threadId && "active"),
                  type: "button",
                  onClick: function () { openThread(thread.threadId); }
                }, [
                  h("div", { className: "message-meta", key: "meta" }, [
                    h("span", { className: "message-from" }, thread.participants.join(", ")),
                    h("span", { className: "muted" }, formatDate(thread.latestReceivedAtUtc))
                  ]),
                  h("div", { className: "message-subject", key: "subject" }, thread.subject || "(no subject)"),
                  h("div", { className: "message-preview", key: "preview" }, thread.preview),
                  h("div", { className: "thread-badges", key: "badges" }, [
                    thread.unreadCount > 0 ? h("span", { className: "thread-pill unread-pill" }, thread.unreadCount + " unread") : null,
                    thread.messageCount > 1 ? h("span", { className: "thread-pill" }, thread.messageCount + " messages") : null,
                    thread.hasStarredMessage ? h("span", { className: "thread-pill star-pill" }, "Starred") : null
                  ])
                ]);
              }))
        ]),
        h("section", { className: "card message-detail", key: "detail" }, !selectedMessage
          ? h("div", { className: "empty-state large" }, "Select a conversation to start reading.")
          : [
              h("div", { className: "detail-header", key: "header" }, [
                h("div", null, [
                  h("div", { className: "detail-badge-row" }, [
                    h("div", { className: "mini-label" }, selectedMessage.folder),
                    selectedMessage.isStarred ? h("span", { className: "thread-pill star-pill" }, "Starred") : null
                  ]),
                  h("h2", null, selectedMessage.subject || "(no subject)"),
                  h("div", { className: "muted" }, "From " + (selectedMessage.headerFrom || selectedMessage.envelopeFrom)),
                  h("div", { className: "muted" }, "To " + (selectedMessage.headerTo || selectedMessage.deliveredAddresses.join(", "))),
                  h("div", { className: "muted" }, formatDate(selectedMessage.receivedAtUtc))
                ]),
                h("div", { className: "detail-actions" }, [
                  h("button", { className: "button secondary", type: "button", onClick: openReply }, "Reply"),
                  h("button", { className: "button ghost", type: "button", onClick: toggleStar }, selectedMessage.isStarred ? "Unstar" : "Star"),
                  h("button", { className: "button ghost", type: "button", onClick: function () { toggleRead(!selectedMessage.isRead); } }, selectedMessage.isRead ? "Mark Unread" : "Mark Read"),
                  selectedMessage.folder === "trash"
                    ? h("button", { className: "button ghost", type: "button", onClick: function () { moveToFolder("inbox"); } }, "Restore")
                    : h("button", { className: "button danger", type: "button", onClick: function () { moveToFolder("trash"); } }, "Delete")
                ])
              ]),
              h("div", { className: "thread-strip", key: "thread-strip" }, selectedThreadMessages.map(function (message) {
                return h("button", {
                  key: message.messageId,
                  className: cx("thread-chip", selectedMessageId === message.messageId && "active"),
                  type: "button",
                  onClick: function () {
                    setSelectedMessageId(message.messageId);
                    setLabelDraft((message.labels || []).join(", "));
                  }
                }, [
                  h("strong", null, message.headerFrom || message.envelopeFrom),
                  h("span", { className: "muted" }, formatDate(message.receivedAtUtc))
                ]);
              })),
              h("div", { className: "label-editor", key: "labels" }, [
                h("input", {
                  className: "search-box",
                  placeholder: "Labels, comma separated",
                  value: labelDraft,
                  onChange: function (event) { setLabelDraft(event.target.value); }
                }),
                h("button", { className: "button secondary", type: "button", onClick: saveLabels }, "Save Labels")
              ]),
              selectedMessage.labels && selectedMessage.labels.length > 0
                ? h("div", { className: "thread-badges", key: "saved-labels" }, selectedMessage.labels.map(function (label) {
                    return h("span", { className: "thread-pill", key: label }, label);
                  }))
                : null,
              h("div", { className: "detail-body", key: "body" }, [
                h("pre", { style: { whiteSpace: "pre-wrap", margin: 0 } }, selectedMessage.plainTextBody || "(No plain text body)"),
                selectedMessage.htmlBody
                  ? h("div", {
                      className: "detail-html",
                      dangerouslySetInnerHTML: { __html: selectedMessage.htmlBody }
                    })
                  : null
              ])
            ])
      ]),
      composeOpen ? h("div", { className: "compose-overlay", key: "compose-overlay" }, [
        h("div", { className: "card compose-modal" }, [
          h("div", { className: "detail-header" }, [
            h("div", null, [
              h("div", { className: "mini-label" }, "Compose"),
              h("h2", null, composeDraft.replyToMessageId ? "Reply" : "New message")
            ]),
            h("button", {
              className: "button ghost",
              type: "button",
              onClick: function () { setComposeOpen(false); }
            }, "Close")
          ]),
          h("form", { className: "stack", onSubmit: sendCompose }, [
            field("To", h("input", {
              value: composeDraft.to,
              onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { to: event.target.value })); }
            }), "compose-to"),
            h("div", { className: "field-grid two", key: "compose-row" }, [
              field("Cc", h("input", {
                value: composeDraft.cc,
                onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { cc: event.target.value })); }
              }), "compose-cc"),
              field("Bcc", h("input", {
                value: composeDraft.bcc,
                onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { bcc: event.target.value })); }
              }), "compose-bcc")
            ]),
            field("Subject", h("input", {
              value: composeDraft.subject,
              onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { subject: event.target.value })); }
            }), "compose-subject"),
            field("Plain text body", h("textarea", {
              value: composeDraft.plainTextBody,
              onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { plainTextBody: event.target.value })); }
            }), "compose-plain"),
            field("Optional HTML body", h("textarea", {
              value: composeDraft.htmlBody,
              onChange: function (event) { setComposeDraft(Object.assign({}, composeDraft, { htmlBody: event.target.value })); }
            }), "compose-html"),
            h("div", { className: "action-row" }, [
              h("button", { className: "button primary", type: "submit" }, "Send"),
              h("button", {
                className: "button ghost",
                type: "button",
                onClick: function () { setComposeOpen(false); }
              }, "Cancel")
            ])
          ])
        ])
      ]) : null,
      (error || success) ? h("div", { className: "toast-stack", key: "toasts" }, [
        error ? h("div", { className: "error-banner", key: "error" }, error) : null,
        success ? h("div", { className: "success-banner", key: "success" }, success) : null
      ]) : null
    ]);
  }

  ReactDOM.createRoot(document.getElementById("root")).render(h(App));
})();
