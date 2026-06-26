/* Registro.br DNS Helper — popup */
(function () {
  "use strict";

  const TYPES = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV"];
  const STORAGE_KEY = "rbr_dns_rows";
  const DOMAIN_KEY = "rbr_domain";
  const AUTOSAVE_KEY = "rbr_autosave";

  const $ = (id) => document.getElementById(id);
  const el = {
    status: $("status"),
    banner: $("banner"),
    rows: $("rows"),
    log: $("log"),
    logSection: $("logSection"),
    importBox: $("importBox"),
    importText: $("importText"),
    importTfBox: $("importTfBox"),
    importTfText: $("importTfText"),
    domainInput: $("domainInput"),
    chkAutoSave: $("chkAutoSave"),
    chkOnTop: $("chkOnTop"),
  };

  const delay = (ms) => new Promise((r) => setTimeout(r, ms));

  // Modo "destacado": esta página está rodando como janela própria
  // (chrome.windows.create), não como popup ancorado no ícone.
  const PARAMS = new URLSearchParams(location.search);
  const DETACHED = PARAMS.get("detached") === "1";
  const TARGET_TAB = parseInt(PARAMS.get("tab") || "", 10);

  let currentTab = null;

  // --------------------------------------------------------------------------
  // Linhas de registro
  // --------------------------------------------------------------------------
  function makeRow(rec) {
    rec = rec || { name: "", type: "A", value: "", ttl: "", priority: "" };
    const row = document.createElement("div");
    row.className = "row";

    const type = document.createElement("select");
    TYPES.forEach((t) => {
      const o = document.createElement("option");
      o.value = t; o.textContent = t;
      if (t === rec.type) o.selected = true;
      type.appendChild(o);
    });

    const name = inputEl("text", rec.name, "ex: www  (vazio = @)");
    const value = inputEl("text", rec.value, "ex: 203.0.113.10 / destino");
    const pri = inputEl("number", rec.priority, "10");

    const del = document.createElement("button");
    del.className = "del"; del.type = "button"; del.textContent = "×";
    del.title = "Remover";
    del.addEventListener("click", () => { row.remove(); persist(); ensureOne(); });

    function syncPri() {
      const needs = type.value === "MX" || type.value === "SRV";
      pri.classList.toggle("pri-hidden", !needs);
    }
    type.addEventListener("change", () => { syncPri(); persist(); });
    [name, value, pri].forEach((i) => i.addEventListener("input", persist));
    syncPri();

    row.append(type, name, value, pri, del);
    row._get = () => ({
      name: name.value.trim(),
      type: type.value,
      value: value.value.trim(),
      priority: pri.value.trim(),
    });
    return row;
  }

  function inputEl(type, val, ph) {
    const i = document.createElement("input");
    i.type = type; i.value = val == null ? "" : val; i.placeholder = ph || "";
    if (type === "number") i.min = "0";
    return i;
  }

  function collectRows() {
    return [...el.rows.children].map((r) => r._get());
  }

  function setRows(list) {
    el.rows.innerHTML = "";
    (list && list.length ? list : [null]).forEach((r) => el.rows.appendChild(makeRow(r)));
  }

  function ensureOne() {
    if (!el.rows.children.length) el.rows.appendChild(makeRow());
  }

  // Remove da tela todos os registros da lista (não mexe no que já foi salvo).
  function clearAllRows() {
    const filled = collectRows().some((r) => r.name || r.value || r.priority);
    if (filled && !confirm("Remover todos os registros da lista?\n(Não afeta o que já foi salvo no registro.br.)")) return;
    setRows([]);
    persist();
    logLine("Lista de registros limpa.");
  }

  function persist() {
    chrome.storage.local.set({ [STORAGE_KEY]: collectRows() });
  }

  // --------------------------------------------------------------------------
  // Importação por texto:  nome tipo valor [ttl] [prioridade]
  // --------------------------------------------------------------------------
  function parseText(text) {
    const out = [];
    text.split(/\r?\n/).forEach((raw) => {
      const line = raw.trim();
      if (!line || line.startsWith("#") || line.startsWith(";")) return;
      const tok = line.split(/\s+/);
      if (tok.length < 3) return;
      const name = tok[0] === "@" ? "" : tok[0];
      const type = tok[1].toUpperCase();
      let value, ttl = "", priority = "";
      if (type === "TXT") {
        value = tok.slice(2).join(" ").replace(/^"|"$/g, "");
      } else if (type === "MX" || type === "SRV") {
        value = tok[2]; ttl = num(tok[3]); priority = num(tok[4]);
        // aceita também "nome MX prioridade valor"
        if (priority === "" && /^\d+$/.test(tok[2] || "") && tok[3]) {
          priority = tok[2]; value = tok[3]; ttl = num(tok[4]);
        }
      } else {
        value = tok[2]; ttl = num(tok[3]);
      }
      out.push({ name, type: TYPES.includes(type) ? type : "A", value, ttl, priority });
    });
    return out;
  }
  const num = (v) => (/^\d+$/.test(v || "") ? v : "");

  // --------------------------------------------------------------------------
  // Importação da saída do Terraform (acs_custom_domain_dns_records)
  // Lê diretamente os pares "name"/"ttl"/"type"/"value"/"priority", então
  // funciona com ou sem as chaves { } (que às vezes se perdem no copiar/colar).
  // --------------------------------------------------------------------------
  function parseTerraform(text, domain) {
    // 1) Quebra o texto nos blocos  "<chave>" = tolist([ ... ]  (dkim, domain, spf…)
    const blockRe = /"([a-zA-Z0-9_]+)"\s*=\s*tolist\(\s*\[/g;
    const heads = [];
    let bm;
    while ((bm = blockRe.exec(text)) !== null) {
      heads.push({ key: bm[1].toLowerCase(), start: bm.index + bm[0].length });
    }
    const blocks = [];
    if (heads.length) {
      for (let i = 0; i < heads.length; i++) {
        const end = i + 1 < heads.length ? heads[i + 1].start : text.length;
        blocks.push({ key: heads[i].key, body: text.slice(heads[i].start, end) });
      }
    } else {
      blocks.push({ key: "", body: text }); // sem marcadores — um bloco só
    }

    // 2) Extrai os registros (name/ttl/type/value/priority) de cada bloco
    function recordsOf(body) {
      const re = /"(name|ttl|type|value|priority|preference)"\s*=\s*(?:"([^"]*)"|([0-9]+))/g;
      const out = [];
      let cur = {}, m;
      const flush = () => {
        if (cur.type && cur.value !== undefined) out.push(cur);
        cur = {};
      };
      while ((m = re.exec(body)) !== null) {
        let key = m[1];
        if (key === "preference") key = "priority";
        const val = m[2] !== undefined ? m[2] : m[3];
        if (cur[key] !== undefined) flush(); // chave repetida = novo registro
        cur[key] = val;
      }
      flush();
      return out;
    }
    const parsed = blocks.map((b) => ({ key: b.key, recs: recordsOf(b.body) }));

    // 3) Subdomínio de e-mail = nome do registro domain/spf relativo ao domínio.
    //    Ex.: "hmail.meudominio.com.br" com domínio "meudominio.com.br" => "hmail".
    let emailSub = "";
    for (const b of parsed) {
      if ((b.key === "domain" || b.key === "spf") && b.recs.length) {
        emailSub = relativizeName(b.recs[0].name || "", domain);
        if (emailSub) break;
      }
    }

    // 4) Monta a saída. Registros DKIM vêm relativos ao subdomínio de e-mail,
    //    então recebem o sufixo ".<emailSub>" (ex.: "..._domainkey.hmail").
    const records = [];
    for (const b of parsed) {
      for (const r of b.recs) {
        let name = relativizeName(r.name || "", domain);
        if (/^dkim/.test(b.key) && emailSub) {
          const suffix = "." + emailSub;
          if (name !== emailSub && !name.toLowerCase().endsWith(suffix.toLowerCase())) {
            name = name ? name + suffix : emailSub;
          }
        }
        records.push({
          name,
          type: String(r.type).toUpperCase(),
          value: r.value || "",
          ttl: r.ttl || "",
          priority: r.priority || "",
        });
      }
    }
    return records;
  }

  // Torna o nome relativo ao domínio: "email.dominio.com.br" -> "email";
  // nome igual ao domínio -> "" (raiz). Domínio diferente é mantido como está.
  function relativizeName(name, domain) {
    name = (name || "").replace(/\.$/, "");
    if (!domain) return name;
    const d = domain.replace(/^https?:\/\//, "").replace(/\/.*$/, "").replace(/\.$/, "").toLowerCase();
    const n = name.toLowerCase();
    if (!d) return name;
    if (n === d) return "";
    if (n.endsWith("." + d)) return name.slice(0, name.length - d.length - 1);
    return name;
  }

  // Converte o Terraform colado. append=false substitui a lista; append=true
  // mantém os registros já preenchidos e acrescenta os novos.
  function importTf(append) {
    const parsed = parseTerraform(el.importTfText.value, el.domainInput.value.trim());
    if (!parsed.length) {
      logLine("Nenhum registro reconhecido na saída do Terraform.");
      return;
    }
    if (append) {
      const keep = collectRows().filter((r) => r.name || r.value || r.priority);
      setRows(keep.concat(parsed));
    } else {
      setRows(parsed);
    }
    persist();
    el.importTfText.value = ""; // limpa a caixa do Terraform após converter
    el.importTfBox.classList.add("hidden");
    logLine(parsed.length + " registro(s) " + (append ? "adicionado(s)" : "importado(s)") + " do Terraform.");
  }

  async function waitTabComplete(tabId, timeout = 15000) {
    const start = Date.now();
    while (Date.now() - start < timeout) {
      try {
        const t = await chrome.tabs.get(tabId);
        if (t && t.status === "complete") return true;
      } catch (e) { /* aba em transição */ }
      await delay(300);
    }
    return false;
  }

  // Após navegar, o content script da nova página pode demorar a responder.
  async function sendWithRetry(msg, tries = 6) {
    let lastErr;
    for (let i = 0; i < tries; i++) {
      try { return await send(msg); }
      catch (e) { lastErr = e; await delay(700); }
    }
    throw lastErr || new Error("sem resposta do content script");
  }

  // "Abrir DNS": navega para a página do domínio E entra no editor de zona.
  async function openDnsPage() {
    const domain = (el.domainInput.value || "").trim().toLowerCase();
    if (!domain) { logLine("Informe o domínio primeiro."); el.domainInput.focus(); return; }
    const url = "https://registro.br/painel/dominios/?dominio=" + encodeURIComponent(domain);
    chrome.storage.local.set({ [DOMAIN_KEY]: domain });
    if (!currentTab) currentTab = await getActiveTab();
    el.log.textContent = "";
    setStatus("Abrindo " + domain + "…", "");
    logLine("Abrindo a página de DNS de " + domain + "…");
    await chrome.tabs.update(currentTab.id, { url });
    await waitTabComplete(currentTab.id);
    await delay(1200); // tempo do painel (SPA) renderizar
    logLine("Entrando no editor de zona…");
    try {
      const res = await sendWithRetry({ type: "PREPARE_EDITOR" });
      if (res && res.ok) {
        logLine(res.ready
          ? "Editor de zona pronto — já pode aplicar os registros."
          : "Página aberta. Se preciso, clique manualmente em Configurar/Editar zona.");
      }
    } catch (e) {
      logLine("Página aberta. Não consegui entrar no editor automaticamente (" + (e.message || e) + ").");
    }
  }

  // --------------------------------------------------------------------------
  // Comunicação com o content script
  // --------------------------------------------------------------------------
  async function getActiveTab() {
    if (DETACHED) {
      // 1) a aba alvo memorizada ao destacar
      if (TARGET_TAB) {
        try { const t = await chrome.tabs.get(TARGET_TAB); if (t) return t; } catch (e) {}
      }
      // 2) aba ativa de alguma janela NORMAL (não a janela destacada)
      let wins = [];
      try { wins = await chrome.windows.getAll({ populate: true }); } catch (e) {}
      const normals = wins.filter((w) => w.type === "normal");
      const pick = (w) => (w && (w.tabs || []).find((t) => t.active)) || null;
      let tab = pick(normals.find((w) => w.focused));
      if (!tab) for (const w of normals) { tab = pick(w); if (tab) break; }
      if (tab) return tab;
    }
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return tab;
  }

  // Abre o painel numa janela própria, colada na borda direita da tela.
  async function openDetached() {
    const tab = await getActiveTab();
    const W = 560, H = 720;
    const left = Math.max(0, (screen.availLeft || 0) + (screen.availWidth || 1280) - W);
    const top = Math.max(0, screen.availTop || 0);
    const url = chrome.runtime.getURL(
      "popup/popup.html?detached=1" + (tab && tab.id ? "&tab=" + tab.id : "")
    );
    await chrome.windows.create({ url, type: "popup", width: W, height: H, left, top, focused: true });
    window.close(); // fecha o popup ancorado
  }

  function isRegistroBr(url) {
    return /^https?:\/\/([a-z0-9-]+\.)?registro\.br\//i.test(url || "");
  }

  async function send(msg) {
    if (!currentTab) throw new Error("Aba não encontrada.");
    try {
      return await chrome.tabs.sendMessage(currentTab.id, msg);
    } catch (e) {
      // content script ainda não injetado — injeta e tenta de novo.
      await chrome.scripting.executeScript({ target: { tabId: currentTab.id }, files: ["content/content.js"] });
      return await chrome.tabs.sendMessage(currentTab.id, msg);
    }
  }

  // --------------------------------------------------------------------------
  // Log / status
  // --------------------------------------------------------------------------
  function logLine(text) {
    el.logSection.classList.remove("hidden");
    el.log.textContent += (el.log.textContent ? "\n" : "") + text;
    el.log.scrollTop = el.log.scrollHeight;
  }
  function setStatus(text, cls) {
    el.status.textContent = text;
    el.status.className = "status" + (cls ? " " + cls : "");
  }
  function showBanner(html) {
    el.banner.innerHTML = html;
    el.banner.classList.remove("hidden");
  }

  chrome.runtime.onMessage.addListener((msg) => {
    if (msg && msg.type === "RBR_PROGRESS") logLine(msg.text);
  });

  // --------------------------------------------------------------------------
  // Ações
  // --------------------------------------------------------------------------
  async function apply() {
    const records = collectRows().filter((r) => r.value);
    if (!records.length) {
      logLine("Nada para aplicar: preencha ao menos um valor.");
      return;
    }
    const btn = $("btnApply");
    btn.disabled = true; btn.textContent = "Aplicando…";
    el.log.textContent = "";
    try {
      const autoSave = el.chkAutoSave.checked;
      const res = await send({ type: "APPLY_RECORDS", records, options: { autoSave } });
      if (!res || !res.ok) throw new Error((res && res.error) || "sem resposta do content script");
      const items = res.result.items || [];
      const ok = items.filter((i) => i.ok).length;
      logLine("");
      logLine("Concluído: " + ok + "/" + items.length + " adicionado(s).");
      logLine(res.result.saved
        ? '"Salvar" acionado — confira o painel.'
        : 'Registros adicionados. Clique em "Salvar alterações" para gravar.');
    } catch (e) {
      logLine("ERRO: " + (e.message || e));
      logLine('Dica: use "Abrir DNS" para entrar no editor, ou "Diagnosticar".');
    } finally {
      btn.disabled = false; btn.textContent = "Aplicar no registro.br";
    }
  }

  async function saveZone() {
    logLine("Salvando alterações…");
    try {
      const res = await send({ type: "SAVE_ZONE" });
      if (!res || !res.ok) throw new Error((res && res.error) || "sem resposta");
      logLine(res.saved
        ? 'Salvo — botão "Salvar" acionado no painel.'
        : 'Botão "Salvar" não encontrado na página.');
    } catch (e) {
      logLine("ERRO: " + (e.message || e));
    }
  }

  async function diagnose() {
    el.log.textContent = "";
    logLine("Diagnosticando a página…");
    try {
      const res = await send({ type: "DIAGNOSE" });
      if (!res || !res.ok) throw new Error((res && res.error) || "sem resposta");
      el.log.textContent = res.report;
      el.logSection.classList.remove("hidden");
    } catch (e) {
      logLine("ERRO: " + (e.message || e));
    }
  }

  // --------------------------------------------------------------------------
  // Init
  // --------------------------------------------------------------------------
  async function init() {
    $("btnAddRow").addEventListener("click", () => { el.rows.appendChild(makeRow()); persist(); });
    $("btnClearRows").addEventListener("click", clearAllRows);
    $("btnApply").addEventListener("click", apply);
    $("btnSave").addEventListener("click", saveZone);
    $("btnDiagnose").addEventListener("click", diagnose);
    $("btnClearLog").addEventListener("click", () => { el.log.textContent = ""; });
    el.chkAutoSave.addEventListener("change", () =>
      chrome.storage.local.set({ [AUTOSAVE_KEY]: el.chkAutoSave.checked })
    );
    el.chkOnTop.addEventListener("change", () => {
      if (DETACHED) {
        if (!el.chkOnTop.checked) window.close(); // volta a ser popup normal
      } else if (el.chkOnTop.checked) {
        openDetached();
      }
    });
    $("btnImport").addEventListener("click", () => el.importBox.classList.toggle("hidden"));
    $("btnImportTf").addEventListener("click", () => el.importTfBox.classList.toggle("hidden"));
    $("btnOpenDns").addEventListener("click", openDnsPage);
    el.domainInput.addEventListener("input", () =>
      chrome.storage.local.set({ [DOMAIN_KEY]: el.domainInput.value.trim().toLowerCase() })
    );
    $("btnParse").addEventListener("click", () => {
      const parsed = parseText(el.importText.value);
      if (parsed.length) { setRows(parsed); persist(); el.importBox.classList.add("hidden"); }
      else logLine("Nenhuma linha válida reconhecida.");
    });
    $("btnParseTf").addEventListener("click", () => importTf(false));
    $("btnAddTf").addEventListener("click", () => importTf(true));

    if (DETACHED) document.body.classList.add("detached");
    el.chkOnTop.checked = DETACHED; // marcado = está destacada

    const stored = await chrome.storage.local.get([STORAGE_KEY, DOMAIN_KEY, AUTOSAVE_KEY]);
    setRows(stored[STORAGE_KEY]);
    if (stored[DOMAIN_KEY]) el.domainInput.value = stored[DOMAIN_KEY];
    el.chkAutoSave.checked = !!stored[AUTOSAVE_KEY];

    currentTab = await getActiveTab();
    if (!isRegistroBr(currentTab && currentTab.url)) {
      setStatus("Você não está no registro.br", "");
      showBanner('Abra o painel e a Edição de Zona do seu domínio para aplicar. <a id="open">Abrir registro.br</a>');
      const open = $("open");
      if (open) open.addEventListener("click", () => chrome.tabs.create({ url: "https://registro.br/" }));
      return;
    }

    try {
      const res = await send({ type: "PING" });
      if (res && res.ok) {
        if (res.domain && !el.domainInput.value.trim()) el.domainInput.value = res.domain;
        setStatus(res.domain ? "Domínio: " + res.domain : "Conectado ao registro.br", "");
      } else {
        setStatus("Conectado", "");
      }
    } catch (e) {
      setStatus("Recarregue a página do registro.br", "");
    }
  }

  init();
})();
