(() => {
  "use strict";

  const byId = (id) => document.getElementById(id);
  const field = (scope, name) => scope.form.querySelector(`[data-field="${name}"]`);
  const keys = new Map();
  const controllers = new Set();
  const identity = byId("identity");
  const keyInput = byId("api-key");
  const scopes = [];
  const slots = [];
  let identityGeneration = 0;
  let loadingDemoKeys = false;
  let page = null;
  let teams = [];

  class UserVisibleError extends Error {}

  function makeScope(form) {
    const scope = { form, status: form.querySelector("[data-status]"), busy: false, snapshot: null };
    scopes.push(scope);
    return scope;
  }

  function addPreconditions(form, prefix) {
    form.querySelector("[data-preconditions]").append(byId("precondition-template").content.cloneNode(true));
    for (const control of form.querySelectorAll("[data-field]")) {
      control.id = `${prefix}-${control.dataset.field}`;
    }
    for (const label of form.querySelectorAll("[data-label]")) {
      label.htmlFor = `${prefix}-${label.dataset.label}`;
    }
    form.querySelector('[data-field="match-mode"]').addEventListener("change", refreshControls);
  }

  const listing = makeScope(byId("list-form"));
  const creation = makeScope(byId("create-form"));
  const discovery = makeScope(byId("teams-form"));
  const sharing = makeScope(byId("sharing-form"));
  addPreconditions(sharing.form, "sharing");

  for (const name of ["A", "B"]) {
    const fragment = byId("editor-template").content.cloneNode(true);
    const form = fragment.querySelector("form");
    form.id = `editor-${name.toLowerCase()}`;
    form.querySelector("[data-heading]").textContent = name === "A" ? "Note" : "Second snapshot (B)";
    addPreconditions(form, form.id);
    byId(name === "A" ? "primary-editor" : "secondary-editor").append(fragment);
    const slot = makeScope(form);
    slot.name = name;
    slots.push(slot);
    field(slot, "clone").textContent = name === "A" ? "Keep a second snapshot" : "Copy snapshot to main editor";
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      if (event.submitter === field(slot, "load")) void loadNote(slot);
      else void saveNote(slot);
    });
    field(slot, "id").addEventListener("input", refreshControls);
    field(slot, "id").addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        field(slot, "load").click();
      }
    });
    field(slot, "sharing").addEventListener("click", () => {
      if (slot.snapshot) void loadSharing(slot.snapshot.id);
    });
    field(slot, "reload").addEventListener("click", () => {
      if (slot.snapshot) void loadNote(slot, slot.snapshot.id);
    });
    field(slot, "delete").addEventListener("click", () => void deleteNote(slot));
    field(slot, "clone").addEventListener("click", () => {
      const other = slots.find((candidate) => candidate !== slot);
      if (!slot.snapshot || other.busy || !mayReplaceNote(other)) return;
      setNoteSnapshot(other, slot.snapshot);
      showStatus(other, `Cloned ${slot.name}'s fetched snapshot, not its unsaved draft. No request sent.`, "success");
      focusNote(other);
    });
  }

  function showStatus(scope, message, state = "") {
    scope.status.textContent = message;
    scope.status.dataset.state = state;
  }

  function matchingSnapshot(scope, id) {
    return scope.snapshot && scope.snapshot.id === id;
  }

  function refreshControls() {
    identity.disabled = loadingDemoKeys;
    keyInput.disabled = loadingDemoKeys;
    byId("use-key").disabled = loadingDemoKeys;
    byId("load-demo-keys").disabled = loadingDemoKeys;
    for (const scope of [...slots, sharing]) {
      field(scope, "match-value").disabled = field(scope, "match-mode").value !== "manual";
    }
    for (const slot of slots) {
      const loaded = matchingSnapshot(slot, field(slot, "id").value.trim());
      field(slot, "content").hidden = !slot.snapshot;
      field(slot, "empty").hidden = !!slot.snapshot;
      field(slot, "save").disabled = !loaded;
      field(slot, "reload").disabled = !loaded;
      field(slot, "sharing").disabled = !loaded || sharing.busy;
      field(slot, "delete").disabled = !loaded;
      field(slot, "clone").disabled = !loaded || slots.some((other) => other !== slot && other.busy);
    }
    byId("open-created").disabled = !creation.snapshot || slots[0].busy;
    byId("created-result").hidden = !creation.snapshot;
    byId("new-note").disabled = creation.busy;
    byId("clone-selected").disabled = field(slots[0], "clone").disabled || slots[0].busy;
    byId("load-selected-sharing").disabled = !slots[0].snapshot || sharing.busy;
    byId("demo-sharing").disabled = sharing.busy || discovery.busy;
    byId("previous-page").disabled = listing.busy || !page || page.offset === 0;
    byId("next-page").disabled = listing.busy || !page || page.nextOffset === null;
    byId("grant-team").disabled = byId("grant-audience").value !== "team" || teams.length === 0;
    byId("replace-sharing").disabled = !byId("sharing-id").value.trim()
      || (field(sharing, "match-mode").value === "snapshot"
        && !matchingSnapshot(sharing, byId("sharing-id").value.trim()));
    for (const button of byId("note-list").querySelectorAll("button")) button.disabled = slots[0].busy;
  }

  function clearInspector() {
    byId("response-summary").textContent = "Not sent";
    byId("response-request").textContent = "None";
    byId("response-status").textContent = "Not sent";
    byId("response-etag").textContent = "None";
    byId("response-body").textContent = "No response.";
  }

  function resetResources(message) {
    const hadRequests = controllers.size > 0;
    identityGeneration += 1;
    loadingDemoKeys = false;
    for (const controller of controllers) controller.abort();
    controllers.clear();
    page = null;
    teams = [];
    for (const scope of scopes) {
      scope.snapshot = null;
      scope.busy = false;
      scope.form.reset();
      scope.form.querySelector("fieldset").disabled = false;
      scope.form.removeAttribute("aria-busy");
      showStatus(scope, "");
    }
    for (const slot of slots) {
      field(slot, "metadata").textContent = "No note snapshot loaded.";
      field(slot, "etag").textContent = "Not loaded";
      field(slot, "options").open = false;
    }
    clearSharingSnapshot();
    byId("note-list").replaceChildren();
    byId("team-list").replaceChildren();
    byId("list-page").textContent = "";
    byId("grant-team").replaceChildren(new Option("Get teams first", ""));
    for (const id of ["create-panel", "sharing-panel", "advanced-tests", "response-panel"]) byId(id).open = false;
    clearInspector();
    for (const option of identity.options) {
      option.textContent = `${option.value}${keys.get(option.value)?.trim() ? " - key loaded" : ""}`;
    }
    refreshControls();
    byId("identity-status").dataset.state = "";
    byId("identity-status").textContent = message
      + (hadRequests ? " In-flight requests were canceled locally; a write may already have reached the service. Reload the resource to check." : "");
  }

  function forgetKeys() {
    keys.clear();
    keyInput.value = "";
    resetResources("All keys and results forgotten. Requests will be unauthenticated.");
    byId("manual-key").open = true;
  }

  identity.addEventListener("change", () => {
    keyInput.value = keys.get(identity.value) ?? "";
    resetResources(`Switched to the ${identity.value} label. ${keyInput.value.trim() ? "Its in-memory key is selected." : "No key configured; requests will be unauthenticated."}`);
    if (keyInput.value.trim()) refreshData();
  });
  function keyChanged() {
    if ((keys.get(identity.value) ?? "") === keyInput.value) return;
    keys.set(identity.value, keyInput.value);
    resetResources(keyInput.value.trim()
      ? `Key changed for the ${identity.value} label. Results cleared; the server will determine identity.`
      : "Key cleared. Results cleared; requests will be unauthenticated.");
  }
  keyInput.addEventListener("input", keyChanged);
  keyInput.addEventListener("change", keyChanged);
  byId("use-key").addEventListener("click", () => {
    keyChanged();
    refreshData();
  });
  byId("forget-keys").addEventListener("click", forgetKeys);
  byId("clear-response").addEventListener("click", clearInspector);
  window.addEventListener("pagehide", forgetKeys);

  function readDemoKeys(data) {
    const loaded = new Map();
    for (const name of ["Alice", "Bob", "Carol", "Dana"]) {
      const value = data?.keys?.[name];
      if (typeof value !== "string" || value.length < 32 || /\s/.test(value)) {
        throw new UserVisibleError("The service returned an invalid demo-key configuration. No keys were loaded.");
      }
      try {
        new Headers({ Authorization: `Bearer ${value}` });
      } catch {
        throw new UserVisibleError("The service returned an unusable demo key. No keys were loaded.");
      }
      loaded.set(name, value);
    }
    if (new Set(loaded.values()).size !== 4) throw new UserVisibleError("The service must configure four different demo keys. No keys were loaded.");
    return loaded;
  }

  async function loadDemoKeys() {
    if (loadingDemoKeys) return;
    keys.clear();
    keyInput.value = "";
    resetResources("Loading demo users...");
    const generation = identityGeneration;
    const controller = new AbortController();
    controllers.add(controller);
    loadingDemoKeys = true;
    refreshControls();
    byId("identity-status").dataset.state = "pending";
    let loaded;
    try {
      const response = await fetch("/demo/keys", {
        headers: { Accept: "application/json", "X-Notes-Demo": "true" },
        signal: controller.signal,
        credentials: "omit",
        mode: "same-origin",
        redirect: "error",
        cache: "no-store",
        referrerPolicy: "no-referrer",
      });
      if (response.status === 404) {
        throw new UserVisibleError("Automatic demo access is disabled on this service. Use a manual API key below.");
      }
      if (response.status !== 200) throw new UserVisibleError(`Demo users could not be loaded (HTTP ${response.status}). Check the local demo configuration or use a manual key.`);
      loaded = readDemoKeys(await response.json());
    } catch (error) {
      if (generation !== identityGeneration) return;
      byId("identity-status").dataset.state = "error";
      byId("identity-status").textContent = error instanceof UserVisibleError
        ? error.message
        : "Could not load demo users. Check the local service or use a manual API key below.";
      byId("manual-key").open = true;
      return;
    } finally {
      controllers.delete(controller);
      if (generation === identityGeneration) {
        loadingDemoKeys = false;
        refreshControls();
      }
    }
    if (generation !== identityGeneration) return;
    for (const [name, value] of loaded) keys.set(name, value);
    keyInput.value = keys.get(identity.value);
    resetResources("Demo users are ready. Choose Alice, Bob, Carol, or Dana to see their notes.");
    byId("manual-key").open = false;
    refreshData();
  }

  byId("load-demo-keys").addEventListener("click", () => void loadDemoKeys());

  function refreshData() {
    void loadPage(Number(byId("list-limit").value), Number(byId("list-offset").value));
    void loadTeams();
  }

  async function run(scope, pendingMessage, operation) {
    if (scope.busy) return;
    const generation = identityGeneration;
    scope.busy = true;
    scope.form.querySelector("fieldset").disabled = true;
    scope.form.setAttribute("aria-busy", "true");
    showStatus(scope, pendingMessage, "pending");
    refreshControls();
    try {
      await operation(generation);
    } catch (error) {
      if (generation !== identityGeneration) return;
      showStatus(scope, error instanceof UserVisibleError
        ? error.message
        : "Unexpected browser error. The operation did not finish normally; a write may already have reached the service. Reload the resource before another write.", "error");
    } finally {
      if (generation === identityGeneration) {
        scope.busy = false;
        scope.form.querySelector("fieldset").disabled = false;
        scope.form.removeAttribute("aria-busy");
        refreshControls();
      }
    }
  }

  async function request(generation, path, { method = "GET", body, etag } = {}) {
    // Password managers may change a field without firing an input/change event.
    if (keyInput.value !== (keys.get(identity.value) ?? "")) {
      keyChanged();
      return null;
    }
    const controller = new AbortController();
    controllers.add(controller);
    let response;
    let text;
    try {
      const headers = { Accept: "application/json" };
      const key = keyInput.value.trim();
      if (key) headers.Authorization = `Bearer ${key}`;
      if (body !== undefined) headers["Content-Type"] = "application/json";
      if (etag !== undefined) headers["If-Match"] = etag;
      response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
        credentials: "omit",
        mode: "same-origin",
        redirect: "error",
        cache: "no-store",
        referrerPolicy: "no-referrer",
      });
      text = await response.text();
    } catch {
      if (generation !== identityGeneration) return null;
      byId("response-request").textContent = `${method} ${path}`;
      byId("response-status").textContent = "Request could not be completed";
      byId("response-summary").textContent = "Request failed";
      byId("response-etag").textContent = "Unknown";
      byId("response-body").textContent = "No complete response was received. Check the local service and connection. Redirects are deliberately refused. No automatic retry was made; a write may already have reached the service.";
      throw new UserVisibleError("Request could not be completed. Check the response inspector. Reload the resource before retrying a write.");
    } finally {
      controllers.delete(controller);
    }
    if (generation !== identityGeneration) return null;
    let data;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch (error) {
        if (!(error instanceof SyntaxError)) throw error;
      }
    }
    const result = { ok: response.ok, status: response.status, statusText: response.statusText, etag: response.headers.get("ETag"), data };
    byId("response-request").textContent = `${method} ${path}`;
    byId("response-status").textContent = `HTTP ${response.status} ${response.statusText}`.trim();
    byId("response-summary").textContent = `HTTP ${response.status}`;
    byId("response-etag").textContent = result.etag ?? "None";
    byId("response-body").textContent = text
      ? (data === undefined ? text : JSON.stringify(data, null, 2))
      : "(empty response body)";
    return result;
  }

  function success(scope, result, expectedStatus, valid = () => true) {
    if (!result) return false;
    if (!result.ok) {
      const problem = result.data;
      const details = [problem?.title, problem?.detail].filter((value) => typeof value === "string").join(" ");
      let message = `HTTP ${result.status} ${result.statusText}. ${details}`.trim();
      if (result.status === 412) {
        message += " Conflict: this panel's draft and fetched ETag were preserved. Load the latest version in the other editor (or reload sharing), reconcile, then explicitly retry. No write was retried.";
      } else if (result.status === 428) {
        message += " If-Match is required. Use a fetched ETag or provide one strong quoted version.";
      } else if (result.status === 404) {
        message += " The note is absent or inaccessible. Previously displayed snapshots are not proof of current access.";
      }
      showStatus(scope, message, "error");
      return false;
    }
    if (result.status !== expectedStatus || !valid(result)) {
      throw new UserVisibleError("The successful response does not match the expected SPEC.md shape/status/ETag. No new snapshot was accepted; inspect the response.");
    }
    return true;
  }

  function validEtag(value) {
    return typeof value === "string" && /^"[1-9][0-9]*"$/.test(value);
  }

  function validNote(result) {
    const note = result.data;
    return note && ["id", "ownerId", "title", "body", "createdAt", "updatedAt"].every((key) => typeof note[key] === "string")
      && Number.isInteger(note.version) && note.version > 0 && validEtag(result.etag);
  }

  function notePath(id) {
    if (!id) throw new UserVisibleError("Enter a note ID first.");
    return `/api/notes/${encodeURIComponent(id)}`;
  }

  function selectedEtag(scope, id) {
    const mode = field(scope, "match-mode").value;
    if (mode === "omit") return undefined;
    if (mode === "manual") return field(scope, "match-value").value;
    if (!matchingSnapshot(scope, id)) {
      throw new UserVisibleError("Load this resource's snapshot first, or choose an omitted/manual If-Match for a failure test.");
    }
    return scope.snapshot.etag;
  }

  function resetPreconditions(scope) {
    field(scope, "match-mode").value = "snapshot";
    field(scope, "match-value").value = "";
  }

  function mayReplaceNote(slot) {
    const note = slot.snapshot?.data;
    const hasDraft = note
      ? field(slot, "title").value !== note.title || field(slot, "body").value !== note.body
      : field(slot, "title").value !== "" || field(slot, "body").value !== "";
    return !hasDraft || window.confirm(`Replace unsaved content in editor ${slot.name}? Use the other editor to inspect the latest version without losing this draft.`);
  }

  function setNoteSnapshot(slot, snapshot) {
    const note = snapshot.data;
    slot.snapshot = { id: note.id, data: { ...note }, etag: snapshot.etag };
    field(slot, "id").value = note.id;
    field(slot, "title").value = note.title;
    field(slot, "body").value = note.body;
    field(slot, "etag").textContent = snapshot.etag;
    field(slot, "metadata").textContent = `Loaded ID: ${note.id}\nOwner: ${note.ownerId} | Version: ${note.version}\nCreated: ${note.createdAt}\nUpdated: ${note.updatedAt}`;
    resetPreconditions(slot);
    refreshControls();
  }

  function focusNote(slot) {
    if (slot.name === "B") byId("advanced-tests").open = true;
    field(slot, "title").focus();
  }

  async function loadNote(slot, requestedId = field(slot, "id").value.trim()) {
    if (slot.busy || !mayReplaceNote(slot)) return;
    await run(slot, "Loading note...", async (generation) => {
      const result = await request(generation, notePath(requestedId));
      if (!success(slot, result, 200, validNote)) return;
      setNoteSnapshot(slot, result);
      showStatus(slot, `HTTP 200. Loaded version ${result.data.version}.`, "success");
      focusNote(slot);
    });
  }

  async function saveNote(slot) {
    if (!matchingSnapshot(slot, field(slot, "id").value.trim())) return;
    await run(slot, "Saving content...", async (generation) => {
      const id = slot.snapshot.id;
      const result = await request(generation, notePath(id), {
        method: "PUT",
        body: { title: field(slot, "title").value, body: field(slot, "body").value },
        etag: selectedEtag(slot, id),
      });
      if (!success(slot, result, 200, validNote)) return;
      setNoteSnapshot(slot, result);
      showStatus(slot, `HTTP 200. Saved version ${result.data.version}. Other panels were not refreshed.`, "success");
    });
  }

  async function deleteNote(slot) {
    if (!matchingSnapshot(slot, field(slot, "id").value.trim())
      || !window.confirm(`Delete note ${slot.snapshot.id} and all its grants? Only its owner may do this. This cannot be undone.`)) return;
    await run(slot, "Deleting note...", async (generation) => {
      const id = slot.snapshot.id;
      const result = await request(generation, notePath(id), { method: "DELETE", etag: selectedEtag(slot, id) });
      if (!success(slot, result, 204)) return;
      slot.snapshot = null;
      field(slot, "title").value = "";
      field(slot, "body").value = "";
      field(slot, "metadata").textContent = `Deleted ID: ${id}. Other displayed snapshots may be stale.`;
      field(slot, "etag").textContent = "Deleted";
      resetPreconditions(slot);
      showStatus(slot, "HTTP 204. Deleted. Reload lists and other panels to check the new state.", "success");
    });
  }

  function addCell(row, text) {
    const cell = document.createElement("td");
    cell.textContent = text;
    row.append(cell);
    return cell;
  }

  function renderNotes(items) {
    const body = byId("note-list");
    body.replaceChildren();
    for (const note of items) {
      const row = document.createElement("tr");
      const title = addCell(row, note.title);
      const id = document.createElement("span");
      id.className = "note-id";
      id.textContent = `Version ${note.version ?? "not included"}`;
      title.append(id);
      const actions = document.createElement("div");
      actions.className = "actions";
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary";
      button.dataset.target = "0";
      button.textContent = "Open";
      button.setAttribute("aria-label", `Open: ${note.title}`);
      button.addEventListener("click", () => void loadNote(slots[0], note.id));
      actions.append(button);
      addCell(row, "").append(actions);
      body.append(row);
    }
  }

  async function loadPage(limit, offset) {
    await run(listing, "Loading accessible notes...", async (generation) => {
      if (!Number.isInteger(limit) || limit < 1 || limit > 100 || !Number.isInteger(offset) || offset < 0) {
        throw new UserVisibleError("Use a limit from 1 through 100 and a nonnegative integer offset.");
      }
      const result = await request(generation, `/api/notes?limit=${limit}&offset=${offset}`);
      const valid = ({ data }) => data && Array.isArray(data.items)
        && data.items.every((note) => note && ["id", "title", "ownerId"].every((key) => typeof note[key] === "string"))
        && (data.nextOffset === null || (Number.isInteger(data.nextOffset) && data.nextOffset > offset));
      if (!success(listing, result, 200, valid)) return;
      page = { limit, offset, nextOffset: result.data.nextOffset };
      byId("list-limit").value = String(limit);
      byId("list-offset").value = String(offset);
      renderNotes(result.data.items);
      byId("list-page").textContent = `Offset ${offset}; ${result.data.items.length} item(s); nextOffset: ${page.nextOffset ?? "null (last page)"}.`;
      showStatus(listing, `HTTP 200. ${result.data.items.length ? "Accessible summaries loaded." : "No accessible notes on this page."}`, "success");
    });
  }

  listing.form.addEventListener("submit", (event) => {
    event.preventDefault();
    refreshData();
  });
  byId("previous-page").addEventListener("click", () => {
    if (page) void loadPage(page.limit, Math.max(0, page.offset - page.limit));
  });
  byId("next-page").addEventListener("click", () => {
    if (page?.nextOffset !== null && page) void loadPage(page.limit, page.nextOffset);
  });
  byId("new-note").addEventListener("click", () => {
    byId("create-panel").open = true;
    byId("create-title").focus();
  });
  byId("clone-selected").addEventListener("click", () => field(slots[0], "clone").click());

  creation.form.addEventListener("submit", (event) => {
    event.preventDefault();
    void run(creation, "Creating a private note...", async (generation) => {
      const result = await request(generation, "/api/notes", {
        method: "POST",
        body: { title: byId("create-title").value, body: byId("create-body").value },
      });
      if (!success(creation, result, 201, validNote)) return;
      creation.snapshot = result;
      byId("created-id").value = result.data.id;
      showStatus(creation, "HTTP 201. Private note created.", "success");
      openCreatedNote();
    });
  });
  function openCreatedNote() {
    if (!creation.snapshot || slots[0].busy || !mayReplaceNote(slots[0])) return;
    setNoteSnapshot(slots[0], creation.snapshot);
    showStatus(slots[0], "Opened the creation response snapshot. No additional request sent.", "success");
    byId("create-panel").open = false;
    focusNote(slots[0]);
  }
  byId("open-created").addEventListener("click", openCreatedNote);

  async function loadTeams() {
    await run(discovery, "Loading teams...", async (generation) => {
      const result = await request(generation, "/api/teams");
      const valid = ({ data }) => data && Array.isArray(data.items)
        && data.items.every((team) => team && typeof team.id === "string" && typeof team.name === "string")
        && data.nextOffset === null;
      if (!success(discovery, result, 200, valid)) return;
      teams = result.data.items;
      byId("team-list").replaceChildren();
      byId("grant-team").replaceChildren(new Option(teams.length ? "Select a team" : "No teams returned", ""));
      for (const team of teams) {
        const item = document.createElement("li");
        item.textContent = `${team.name}: ${team.id}`;
        byId("team-list").append(item);
        byId("grant-team").append(new Option(`${team.name} (${team.id})`, team.id));
      }
      if (sharing.snapshot) renderSharingSummary();
      showStatus(discovery, `HTTP 200. ${teams.length} team(s) discovered.`, "success");
    });
  }
  discovery.form.addEventListener("submit", (event) => {
    event.preventDefault();
    void loadTeams();
  });

  function validSharing(result) {
    const grants = result.data?.grants;
    return Array.isArray(grants) && grants.every((grant) => grant
      && ["read", "edit"].includes(grant.permission)
      && ((grant.audience === "organization" && grant.teamId == null)
        || (grant.audience === "team" && typeof grant.teamId === "string")))
      && validEtag(result.etag);
  }

  function setSharingSnapshot(id, result) {
    sharing.snapshot = { id, data: result.data, etag: result.etag };
    byId("sharing-id").value = id;
    setSharingDraft(result.data);
    byId("sharing-snapshot").textContent = `Loaded sharing for ID: ${id}`;
    byId("sharing-etag").textContent = result.etag;
    resetPreconditions(sharing);
    refreshControls();
  }

  function clearSharingSnapshot() {
    sharing.snapshot = null;
    byId("sharing-json").value = JSON.stringify({ grants: [] }, null, 2);
    byId("sharing-snapshot").textContent = "No sharing snapshot loaded.";
    byId("sharing-etag").textContent = "Not loaded";
    byId("sharing-summary").replaceChildren(textItem("Load sharing to see current access."));
    resetPreconditions(sharing);
  }

  async function loadSharing(id = byId("sharing-id").value.trim()) {
    if (sharing.busy) return;
    byId("sharing-panel").open = true;
    const original = JSON.stringify(sharing.snapshot?.data ?? { grants: [] }, null, 2);
    if (byId("sharing-json").value !== original
      && !window.confirm("Replace the sharing draft with a fresh server snapshot? Preserve any changes you want to reconcile first.")) return;
    if (!matchingSnapshot(sharing, id)) clearSharingSnapshot();
    byId("sharing-id").value = id;
    refreshControls();
    await run(sharing, "Loading owner-only sharing...", async (generation) => {
      const result = await request(generation, `${notePath(id)}/sharing`);
      if (!success(sharing, result, 200, validSharing)) return;
      setSharingSnapshot(id, result);
      showStatus(sharing, "HTTP 200. Sharing snapshot loaded. Note editor ETags were not changed.", "success");
    });
  }

  function sharingDraft() {
    try {
      return JSON.parse(byId("sharing-json").value);
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error;
      throw new UserVisibleError("Sharing draft is not valid JSON. Fix its syntax first; no request was sent.");
    }
  }

  function textItem(text) {
    const item = document.createElement("li");
    item.textContent = text;
    return item;
  }

  function renderSharingSummary() {
    const list = byId("sharing-summary");
    list.replaceChildren();
    let draft;
    try {
      draft = sharingDraft();
    } catch (error) {
      if (!(error instanceof UserVisibleError)) throw error;
      list.append(textItem("Invalid JSON draft. Open custom permissions to fix it."));
      return;
    }
    if (!Array.isArray(draft?.grants)) {
      list.append(textItem("The draft needs a grants array. Open custom permissions to fix it."));
      return;
    }
    if (!draft.grants.length) list.append(textItem("Private: only the owner has access."));
    for (const grant of draft.grants) {
      if (!grant || !["team", "organization"].includes(grant.audience) || !["read", "edit"].includes(grant.permission)) {
        list.append(textItem("Invalid grant. Open custom permissions to fix it."));
        continue;
      }
      const audience = grant.audience === "organization"
        ? "Organization"
        : teams.find((team) => team.id === grant.teamId)?.name ?? grant.teamId ?? "Missing team";
      list.append(textItem(`${audience}: ${grant.permission === "edit" ? "can edit and read" : "can read"}`));
    }
  }

  function setSharingDraft(draft) {
    byId("sharing-json").value = JSON.stringify(draft, null, 2);
    renderSharingSummary();
  }

  byId("sharing-json").addEventListener("input", renderSharingSummary);
  byId("load-selected-sharing").addEventListener("click", () => {
    if (slots[0].snapshot) void loadSharing(slots[0].snapshot.id);
  });
  byId("demo-sharing").addEventListener("click", async () => {
    const generation = identityGeneration;
    if (!teams.length) await loadTeams();
    if (generation !== identityGeneration) return;
    const team = teams.find((candidate) => candidate.name === "Team A");
    if (!team) {
      showStatus(sharing, "Team A was not returned by team discovery. Check the team response before choosing this preset.", "error");
      return;
    }
    setSharingDraft({ grants: [
      { audience: "team", teamId: team.id, permission: "edit" },
      { audience: "organization", permission: "read" },
    ] });
    showStatus(sharing, "Demo permissions are ready in the draft. Save sharing to apply them.");
  });

  sharing.form.addEventListener("submit", (event) => {
    event.preventDefault();
    void loadSharing();
  });
  byId("sharing-id").addEventListener("input", refreshControls);
  byId("grant-audience").addEventListener("change", refreshControls);
  byId("add-grant").addEventListener("click", () => {
    try {
      const draft = sharingDraft();
      if (!draft || !Array.isArray(draft.grants)) {
        throw new UserVisibleError('The helper needs an object containing a "grants" array. Edit the JSON first.');
      }
      const audience = byId("grant-audience").value;
      const teamId = byId("grant-team").value;
      if (audience === "team" && !teamId) throw new UserVisibleError("Get teams and select a team first.");
      const grant = { audience, permission: byId("grant-permission").value };
      if (audience === "team") grant.teamId = teamId;
      draft.grants = draft.grants.filter((existing) => !existing || existing.audience !== audience
        || (audience === "team" && existing.teamId !== teamId));
      draft.grants.push(grant);
      setSharingDraft(draft);
      showStatus(sharing, "Draft changed only. Save sharing to send it to the service.");
    } catch (error) {
      showStatus(sharing, error instanceof UserVisibleError ? error.message : "Could not update the sharing draft. No request was sent.", "error");
    }
  });
  byId("empty-grants").addEventListener("click", () => {
    setSharingDraft({ grants: [] });
    showStatus(sharing, "Draft contains no grants. Save sharing to make the note private.");
  });
  byId("replace-sharing").addEventListener("click", () => {
    void run(sharing, "Replacing all sharing...", async (generation) => {
      const id = byId("sharing-id").value.trim();
      const result = await request(generation, `${notePath(id)}/sharing`, {
        method: "PUT",
        body: sharingDraft(),
        etag: selectedEtag(sharing, id),
      });
      if (!success(sharing, result, 200, validSharing)) return;
      setSharingSnapshot(id, result);
      showStatus(sharing, "HTTP 200. Sharing replaced. Its new ETag was not copied into either note editor.", "success");
    });
  });

  keyInput.value = "";
  void loadDemoKeys();
})();
