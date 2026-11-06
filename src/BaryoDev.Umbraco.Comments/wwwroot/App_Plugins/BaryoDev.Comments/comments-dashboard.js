// Plain custom element with no build step, same approach as the Medium import dashboard.
const API = "/umbraco/management/api/v1/comments";
const BASE = "/App_Plugins/BaryoDev.Comments";
const PAGE = 50;
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

async function call(host, path, method = "GET") {
  const t = await token(host);
  const response = await fetch(API + path, {
    method,
    credentials: "same-origin",
    headers: t ? { Authorization: `Bearer ${t}` } : {},
  });
  if (response.status === 401 || response.status === 403) throw new Error("You do not have access to this.");
  if (!response.ok) throw new Error(`Request failed (${response.status}).`);
  if (response.status === 204) return null;
  return response.json();
}

class CommentsDashboard extends Base {
  #status = "pending";
  #data = null;
  #error = "";
  #busy = false;
  #skip = 0;

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
    const total = (this.#data?.total ?? 1) - 1;
    if (this.#skip >= total && this.#skip > 0) this.#skip = Math.max(0, this.#skip - PAGE);
    await this.#load();
  }

  #render() {
    const counts = this.#data?.counts ?? {};
    const countOf = (key) => counts[key] ?? 0;
    const items = this.#data?.items ?? [];
    const total = this.#data?.total ?? 0;
    const when = (d) => new Date(d).toLocaleString();
    this.innerHTML = `
      <link rel="stylesheet" href="${BASE}/brand.css" />
      <div class="bdc-admin">
        <div class="bdc-admin__head">
          <img src="${BASE}/logo.svg" alt="baryo.dev" />
          <span>Comments</span>
        </div>
        ${this.#error ? `<p class="bdc-err">${esc(this.#error)}</p>` : ""}
        <div class="bdc-tabs">
          ${TABS.map(([key, label]) => `<button class="bdc-tab" data-tab="${key}" aria-pressed="${this.#status === key}">${label}<span class="bdc-count">${countOf(key)}</span></button>`).join("")}
        </div>
        <div class="bdc-box">
          ${items.length === 0 ? `<p class="bdc-note">${this.#busy ? "Loading." : "Nothing here."}</p>` : items.map((c) => `
            <div class="bdc-item">
              <div class="bdc-item__meta">${esc(c.name)}${c.email ? ` (${esc(c.email)})` : ""} on ${esc(c.pageName ?? c.contentKey)} at ${esc(when(c.createdUtc))}</div>
              <p class="bdc-item__body">${esc(c.body)}</p>
              <div class="bdc-actions">
                ${c.status !== "approved" ? `<button class="bdc-btn primary" data-act="approve" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Approve</button>` : ""}
                ${c.status !== "spam" ? `<button class="bdc-btn" data-act="spam" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Mark spam</button>` : ""}
                <button class="bdc-btn danger" data-act="delete" data-id="${esc(c.id)}" ${this.#busy ? "disabled" : ""}>Delete</button>
              </div>
            </div>`).join("")}
        </div>
        <div class="bdc-actions">
          <button class="bdc-btn" data-page="-1" ${this.#skip === 0 || this.#busy ? "disabled" : ""}>Previous</button>
          <span class="bdc-note">${total === 0 ? 0 : this.#skip + 1} to ${Math.min(this.#skip + PAGE, total)} of ${total}</span>
          <button class="bdc-btn" data-page="1" ${this.#skip + PAGE >= total || this.#busy ? "disabled" : ""}>Next</button>
        </div>
      </div>`;
    this.querySelectorAll("[data-tab]").forEach((b) => b.addEventListener("click", () => {
      this.#status = b.dataset.tab;
      this.#skip = 0;
      this.#load();
    }));
    this.querySelectorAll("[data-page]").forEach((b) => b.addEventListener("click", () => {
      this.#skip = Math.max(0, this.#skip + Number(b.dataset.page) * PAGE);
      this.#load();
    }));
    this.querySelectorAll("[data-act]").forEach((b) => b.addEventListener("click", () => this.#act(b.dataset.id, b.dataset.act)));
  }
}

customElements.define("baryodev-comments-dashboard", CommentsDashboard);
export default CommentsDashboard;
export { CommentsDashboard as element };
