// Plain custom element with no build step, same approach as the Medium import dashboard.
const API = "/umbraco/management/api/v1/comments";
const BASE = "/App_Plugins/BaryoDev.Comments";
const PAGE = 50;
const REASONS = {
  cannotsend: "email is not set up on this site",
  failed: "the send failed, use Send email to try again",
  noemail: "no email address",
  notapproved: "the comment is not approved",
};
const TABS = [["pending", "Pending"], ["approved", "Approved"], ["spam", "Spam"]];

const Base = await import("@umbraco-cms/backoffice/element-api")
  .then(({ UmbElementMixin }) => UmbElementMixin(HTMLElement))
  .catch(() => HTMLElement);

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

const tokens = new WeakMap();

async function token(host) {
  if (!tokens.has(host)) {
    tokens.set(host, Promise.all([
      import("@umbraco-cms/backoffice/context-api"),
      import("@umbraco-cms/backoffice/auth"),
    ]).then(([{ UmbContextConsumer }, { UMB_AUTH_CONTEXT }]) => {
      const consumer = new UmbContextConsumer(host, UMB_AUTH_CONTEXT);
      const auth = consumer.asPromise();
      consumer.request();
      return auth;
    }).catch(() => null));
  }
  const auth = await tokens.get(host);
  return auth ? auth.getLatestToken() : null;
}

async function call(host, path, method = "GET", body) {
  const t = await token(host);
  const headers = t ? { Authorization: `Bearer ${t}` } : {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(API + path, {
    method,
    credentials: "same-origin",
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401 || response.status === 403) throw new Error("You do not have access to this.");
  if (!response.ok) {
    const detail = await response.json().catch(() => null);
    throw new Error(detail?.error || `Request failed (${response.status}).`);
  }
  if (response.status === 204) return null;
  return response.json();
}

class CommentsDashboard extends Base {
  #status = "pending";
  #data = null;
  #error = "";
  #busy = false;
  #skip = 0;
  #note = "";
  #replyFor = null;
  #draft = "";
  #notify = true;

  connectedCallback() {
    super.connectedCallback?.();
    this.#load();
  }

  async #load() {
    this.#busy = true;
    this.#render();
    try {
      this.#data = await call(this, `?status=${this.#status}&skip=${this.#skip}&take=${PAGE}`);
      this.#error = "";
    } catch (e) {
      this.#error = e.message;
    }
    this.#busy = false;
    this.#render();
  }

  async #act(id, action) {
    if (action === "delete" && !confirm("Delete this comment for good?")) return;
    this.#note = "";
    this.#busy = true;
    this.#render();
    try {
      await call(this, action === "delete" ? `/${id}` : `/${id}/${action}`, action === "delete" ? "DELETE" : "POST");
    } catch (e) {
      this.#error = e.message;
      this.#busy = false;
      this.#render();
      return;
    }
    const unknownTotal = (this.#data?.total ?? 0) === 0;
    const left = unknownTotal ? (this.#data?.items?.length ?? 1) - 1 : (this.#data?.total ?? 1) - 1;
    if (this.#skip > 0 && (unknownTotal ? left <= 0 && !this.#data?.hasMore : this.#skip >= left)) this.#skip = Math.max(0, this.#skip - PAGE);
    await this.#load();
  }

  async #sendReply(c) {
    const body = this.#draft.trim();
    if (!body) return;
    this.#busy = true;
    this.#render();
    try {
      const result = await call(this, `/${c.id}/reply`, "POST", { body, notify: Boolean(c.email) && this.#notify });
      if (result.emailStatus == null) this.#note = "Reply posted.";
      else if (result.emailed) this.#note = `Reply posted. Emailed ${c.name}.`;
      else this.#note = `Reply posted. Not emailed: ${REASONS[result.emailStatus] ?? "it was not sent"}.`;
      this.#error = "";
      this.#replyFor = null;
      this.#draft = "";
    } catch (e) {
      this.#error = e.message;
      this.#note = "";
      this.#busy = false;
      this.#render();
      return;
    }
    await this.#load();
  }

  async #sendEmail(id) {
    this.#busy = true;
    this.#render();
    let error = "";
    try {
      await call(this, `/${id}/notify`, "POST");
      this.#note = "Email sent.";
    } catch (e) {
      error = e.message;
      this.#note = "";
    }
    // Reload either way so a 409 shows the row as emailed, then show the error, which the reload clears.
    await this.#load();
    if (error) {
      this.#error = error;
      this.#render();
    }
  }

  #render() {
    const counts = this.#data?.counts ?? {};
    const countOf = (key) => counts[key] ?? 0;
    const items = this.#data?.items ?? [];
    const total = this.#data?.total ?? 0;
    const hasMore = this.#data?.hasMore ?? this.#skip + PAGE < total;
    // Counts are zero for an editor on a custom repository, so show the range without a total then.
    const range = total === 0
      ? (items.length > 0 || hasMore ? `${this.#skip + 1} to ${this.#skip + items.length}` : "0 to 0 of 0")
      : `${this.#skip + 1} to ${Math.min(this.#skip + PAGE, total)} of ${total}`;
    const when = (d) => new Date(d).toLocaleString();
    this.innerHTML = `
      <link rel="stylesheet" href="${BASE}/brand.css" />
      <div class="bdc-admin">
        <div class="bdc-admin__head">
          <img src="${BASE}/logo.svg" alt="baryo.dev" />
          <span>Comments</span>
        </div>
        ${this.#error ? `<p class="bdc-err">${esc(this.#error)}</p>` : ""}
        ${this.#note ? `<p class="bdc-ok">${esc(this.#note)}</p>` : ""}
        <div class="bdc-tabs">
          ${TABS.map(([key, label]) => `<button class="bdc-tab" data-tab="${key}" aria-pressed="${this.#status === key}">${label}<span class="bdc-count">${countOf(key)}</span></button>`).join("")}
        </div>
        <div class="bdc-box">
          ${items.length === 0 ? `<p class="bdc-note">${this.#busy ? "Loading." : "Nothing here."}</p>` : items.map((c) => `
            <div class="bdc-item">
              <div class="bdc-item__meta">${esc(c.name)}${c.email ? ` (${esc(c.email)})` : ""} on ${esc(c.pageName ?? c.contentKey)} at ${esc(when(c.createdUtc))}</div>
              ${c.parentId ? `<div class="bdc-item__reply">Reply to ${esc(c.parentName ?? "a deleted comment")}${c.isSiteReply ? " (site reply)" : ""}. ${c.notifiedUtc ? `Emailed ${esc(when(c.notifiedUtc))}` : c.parentHasEmail ? "Not emailed" : "No email address"}.${c.parentStatus !== "approved" ? " Hidden on the page while the comment is not approved." : ""}</div>` : ""}
              <p class="bdc-item__body">${esc(c.body)}</p>
              ${this.#replyFor === c.id ? `
              <div class="bdc-reply">
                <textarea class="bdc-reply__text" maxlength="4000" rows="4" ${this.#busy ? "disabled" : ""}>${esc(this.#draft)}</textarea>
                ${c.email ? `<label class="bdc-reply__check"><input type="checkbox" data-notify ${this.#notify ? "checked" : ""} ${this.#busy ? "disabled" : ""} /> Email ${esc(c.name)}</label>` : ""}
                <div class="bdc-actions">
                  <button class="bdc-btn primary" data-send="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Send reply</button>
                  <button class="bdc-btn" data-cancel ${this.#busy ? "disabled" : ""}>Cancel</button>
                </div>
              </div>` : ""}
              <div class="bdc-actions">
                ${c.canReply && this.#replyFor !== c.id ? `<button class="bdc-btn primary" data-reply="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Reply</button>` : ""}
                ${c.canNotify ? `<button class="bdc-btn" data-email="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Send email</button>` : ""}
                ${c.status !== "approved" ? `<button class="bdc-btn primary" data-act="approve" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Approve</button>` : ""}
                ${c.status !== "spam" ? `<button class="bdc-btn" data-act="spam" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Mark spam</button>` : ""}
                <button class="bdc-btn danger" data-act="delete" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Delete</button>
              </div>
            </div>`).join("")}
        </div>
        <div class="bdc-actions">
          <button class="bdc-btn" data-page="-1" ${this.#skip === 0 || this.#busy ? "disabled" : ""}>Previous</button>
          <span class="bdc-note">${range}</span>
          <button class="bdc-btn" data-page="1" ${!hasMore || this.#busy ? "disabled" : ""}>Next</button>
        </div>
      </div>`;
    this.querySelectorAll("[data-tab]").forEach((b) => b.addEventListener("click", () => {
      this.#status = b.dataset.tab;
      this.#error = "";
      this.#note = "";
      this.#skip = 0;
      this.#load();
    }));
    this.querySelectorAll("[data-page]").forEach((b) => b.addEventListener("click", () => {
      this.#error = "";
      this.#note = "";
      this.#skip = Math.max(0, this.#skip + Number(b.dataset.page) * PAGE);
      this.#load();
    }));
    const byId = (id) => items.find((c) => c.id === id);
    this.querySelectorAll("[data-reply]").forEach((b) => b.addEventListener("click", () => {
      this.#error = "";
      this.#note = "";
      this.#replyFor = b.dataset.reply;
      this.#draft = "";
      this.#notify = true;
      this.#render();
    }));
    this.querySelectorAll("[data-cancel]").forEach((b) => b.addEventListener("click", () => {
      this.#error = "";
      this.#note = "";
      this.#replyFor = null;
      this.#draft = "";
      this.#render();
    }));
    this.querySelectorAll(".bdc-reply__text").forEach((t) => t.addEventListener("input", () => { this.#draft = t.value; }));
    this.querySelectorAll("[data-notify]").forEach((i) => i.addEventListener("change", () => { this.#notify = i.checked; }));
    this.querySelectorAll("[data-send]").forEach((b) => b.addEventListener("click", () => {
      const c = byId(b.dataset.send);
      if (c && !this.#busy) this.#sendReply(c);
    }));
    this.querySelectorAll("[data-email]").forEach((b) => b.addEventListener("click", () => {
      if (!this.#busy) this.#sendEmail(b.dataset.email);
    }));
    this.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", () => this.#act(b.dataset.id, b.dataset.act)));
  }
}

customElements.define("baryodev-comments-dashboard", CommentsDashboard);
export default CommentsDashboard;
export { CommentsDashboard as element };
