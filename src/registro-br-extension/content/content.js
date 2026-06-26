/*
 * Registro.br DNS Helper — content script
 * -----------------------------------------------------------------------------
 * Roda dentro das páginas do registro.br (a sua sessão já autenticada).
 * Recebe mensagens do popup e automatiza a "Edição de zona / Modo Avançado":
 * abre "Nova entrada", preenche Nome / Tipo / Valor / TTL / Prioridade,
 * clica em "Adicionar" e, no fim, em "Salvar".
 *
 * Como o layout do painel pode mudar, a busca por botões e campos é feita por
 * TEXTO/RÓTULO (sem acento, caixa-baixa) e não por seletores fixos. Se algo
 * não for encontrado, use o botão "Diagnosticar" no popup para inspecionar a
 * página e ajustar as palavras-chave abaixo.
 */
(function () {
  if (window.__RBR_DNS_HELPER_LOADED__) return;
  window.__RBR_DNS_HELPER_LOADED__ = true;

  // ---------------------------------------------------------------------------
  // Utilidades
  // ---------------------------------------------------------------------------
  function norm(s) {
    return (s == null ? "" : String(s))
      .normalize("NFD")
      .replace(/[̀-ͯ]/g, "") // remove acentos
      .toLowerCase()
      .replace(/\s+/g, " ")
      .trim();
  }

  function wait(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  async function waitFor(fn, timeout = 8000, interval = 150) {
    const start = Date.now();
    while (Date.now() - start < timeout) {
      let v = null;
      try { v = fn(); } catch (e) { /* ignore */ }
      if (v) return v;
      await wait(interval);
    }
    return null;
  }

  function isVisible(el) {
    if (!el || !el.getClientRects || !el.getClientRects().length) return false;
    const st = window.getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || st.opacity === "0") return false;
    return true;
  }

  function cssEscape(s) {
    return window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/["\\]/g, "\\$&");
  }

  // Define valor em inputs/selects/textareas de forma compatível com
  // frameworks (React/Vue/Angular), disparando os eventos esperados.
  function setNativeValue(el, value) {
    const proto =
      el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype :
      el instanceof HTMLSelectElement ? HTMLSelectElement.prototype :
      HTMLInputElement.prototype;
    const desc = Object.getOwnPropertyDescriptor(proto, "value");
    if (desc && desc.set) desc.set.call(el, value);
    else el.value = value;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function fillInput(el, value) {
    if (!el) return false;
    value = value == null ? "" : String(value);
    try { el.focus(); } catch (e) {}
    setNativeValue(el, value); // setter nativo + input + change
    // Sequência completa de eventos: alguns campos (autocomplete/typeahead)
    // só "comprometem" o valor ao receber teclado/blur, não só input/change.
    const last = value.slice(-1) || "";
    el.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, key: last }));
    el.dispatchEvent(new InputEvent("input", { bubbles: true, data: value, inputType: "insertText" }));
    el.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true, key: last }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
    try { el.blur(); } catch (e) {}
    el.dispatchEvent(new Event("blur", { bubbles: true }));
    return true;
  }

  function selectType(sel, type) {
    if (!sel) return false;
    const t = norm(type);
    const opt =
      [...sel.options].find((o) => norm(o.value) === t || norm(o.textContent) === t) ||
      [...sel.options].find((o) => norm(o.textContent).includes(t) || norm(o.value).includes(t));
    if (!opt) return false;
    try { sel.focus(); } catch (e) {}
    setNativeValue(sel, opt.value);
    return true;
  }

  // ---------------------------------------------------------------------------
  // Localização de botões/links por texto
  // ---------------------------------------------------------------------------
  function clickables(root) {
    return [
      ...(root || document).querySelectorAll(
        'button, a[href], [role="button"], input[type="button"], input[type="submit"], [onclick]'
      ),
    ].filter(isVisible);
  }

  function clickableText(el) {
    return norm(
      (el.textContent || "") + " " +
      (el.value || "") + " " +
      (el.getAttribute("aria-label") || "") + " " +
      (el.getAttribute("title") || "")
    );
  }

  function findClickableByText(texts, root) {
    const list = clickables(root);
    for (const t of texts) {
      const k = norm(t);
      const hit = list.find((e) => clickableText(e) === k);
      if (hit) return hit;
    }
    for (const t of texts) {
      const k = norm(t);
      const hit = list.find((e) => clickableText(e).includes(k));
      if (hit) return hit;
    }
    return null;
  }

  function findClickableByTextWithin(container, texts) {
    return (
      findClickableByText(texts, container) ||
      (container.parentElement && findClickableByText(texts, container.parentElement)) ||
      null
    );
  }

  // ---------------------------------------------------------------------------
  // Localização de campos por rótulo
  // ---------------------------------------------------------------------------
  function labelText(el) {
    const parts = [];
    if (el.id) {
      const lab = document.querySelector('label[for="' + cssEscape(el.id) + '"]');
      if (lab) parts.push(lab.textContent);
    }
    const wrap = el.closest("label");
    if (wrap) parts.push(wrap.textContent);
    parts.push(el.getAttribute("placeholder") || "");
    parts.push(el.getAttribute("aria-label") || "");
    parts.push(el.getAttribute("title") || "");
    parts.push(el.getAttribute("name") || "");
    parts.push(el.id || "");
    const prev = el.previousElementSibling;
    if (prev && /^(label|span|div|td|th|p)$/i.test(prev.tagName)) parts.push(prev.textContent);
    return norm(parts.join(" "));
  }

  function fieldsIn(container) {
    return [
      ...container.querySelectorAll(
        "input:not([type=hidden]):not([type=button]):not([type=submit]):not([type=checkbox]):not([type=radio]), textarea"
      ),
    ].filter(isVisible);
  }

  function findFieldByKeywords(container, keywords) {
    const fields = fieldsIn(container);
    for (const kw of keywords) {
      const k = norm(kw);
      const hit = fields.find((f) => labelText(f).includes(k));
      if (hit) return hit;
    }
    return null;
  }

  // ---------------------------------------------------------------------------
  // Detecção do formulário de entrada de DNS
  // ---------------------------------------------------------------------------
  const VALUE_KEYS = [
    "valor", "dado", "data", "conteudo", "content", "destino",
    "endereco", "target", "aponta", "rdata", "value", "registro de recurso",
  ];
  const NAME_KEYS = [
    "nome do registro", "subdominio", "sub-dominio", "host", "owner",
    "rotulo", "label", "nome", "dominio", "entrada",
  ];
  const PRIORITY_KEYS = ["prioridade", "priority", "pref"];

  function isTypeSelect(sel) {
    const joined = [...sel.options].map((o) => norm(o.value) + " " + norm(o.textContent)).join(" | ");
    return /\bcname\b/.test(joined) && (/\bmx\b/.test(joined) || /\baaaa\b/.test(joined) || /\btxt\b/.test(joined));
  }

  function typeSelects(root) {
    return [...(root || document).querySelectorAll("select")].filter(isVisible).filter(isTypeSelect);
  }

  function entryContainer(el) {
    return (
      el.closest(
        'form, tr, fieldset, li, [class*="entry"], [class*="registro"], [class*="record"], [class*="rr"], [class*="row"]'
      ) || el.parentElement
    );
  }

  function valueFieldOf(container) {
    return findFieldByKeywords(container, VALUE_KEYS);
  }

  // Procura uma entrada pronta para edição: tem <select> de tipo e um campo de
  // valor vazio/editável. Cobre tanto o layout de "linha que surge" quanto o de
  // "formulário fixo que limpa após adicionar".
  function findEditableEntry() {
    for (const sel of typeSelects(document)) {
      const c = entryContainer(sel);
      const vf = valueFieldOf(c);
      if (vf && !vf.disabled && !vf.readOnly && norm(vf.value) === "") {
        return { container: c, typeSelect: sel, valueField: vf };
      }
    }
    return null;
  }

  function findAnyEntry() {
    const sel = typeSelects(document)[0];
    if (!sel) return null;
    const c = entryContainer(sel);
    return { container: c, typeSelect: sel, valueField: valueFieldOf(c) };
  }

  // Linha que está sendo editada (campo de valor já preenchido). Usada para
  // re-localizar referências depois de preencher, mesmo que o DOM mude.
  function findFilledEntry() {
    for (const sel of typeSelects(document)) {
      const c = entryContainer(sel);
      const vf = valueFieldOf(c);
      if (vf && norm(vf.value) !== "") return { container: c, typeSelect: sel, valueField: vf };
    }
    return null;
  }

  // ---------------------------------------------------------------------------
  // Aplicar registros
  // ---------------------------------------------------------------------------
  function progress(text) {
    try { chrome.runtime.sendMessage({ type: "RBR_PROGRESS", text: text }); } catch (e) {}
  }

  // "Já estou na tela de edição da zona?" — detectada por sinais que existem
  // mesmo SEM um formulário aberto (botão Nova entrada / alternância de modo /
  // um select de tipo já presente). Evita re-clicar em "Configurar zona".
  function inEditor() {
    return (
      typeSelects(document).length > 0 ||
      !!findClickableByText(["nova entrada", "novo registro", "adicionar entrada", "adicionar registro"]) ||
      !!findClickableByText(["modo avancado", "modo basico", "modo avancado de dns"])
    );
  }

  // Navega da página do domínio até o editor de zona, clicando, se necessário:
  // "Configurar zona DNS" -> "Editar zona" -> "Modo Avançado".
  async function prepareEditor() {
    const clickStep = (btn) => {
      const label = (btn.textContent || btn.value || "").trim().replace(/\s+/g, " ").slice(0, 40);
      progress('Navegando: "' + label + '"…');
      btn.click();
    };

    const nav = [
      ["configurar zona dns", "configurar zona", "configurar dns", "habilitar zona dns"],
      ["editar zona", "editar a zona", "editar dns", "editar registros", "editar zona dns"],
    ];
    for (const texts of nav) {
      if (inEditor()) break; // já estou no editor — não clicar de novo
      const btn = findClickableByText(texts);
      if (btn) {
        clickStep(btn);
        await waitFor(inEditor, 5000);
        await wait(700);
      }
    }

    // Modo Avançado só aparece quando estou no modo básico; no avançado o botão
    // vira "Modo Básico", então clicar aqui não corre o risco de alternar de volta.
    const adv = findClickableByText(["modo avancado", "modo avancado de dns"]);
    if (adv) {
      clickStep(adv);
      await wait(900);
    }
    return inEditor();
  }

  function isDisabled(el) {
    return (
      el.disabled === true ||
      el.getAttribute("aria-disabled") === "true" ||
      /\bdisabled\b/.test(el.className || "")
    );
  }

  // Clique "de verdade": simula a sequência ponteiro/mouse + clique nativo, pois
  // alguns botões (ex.: "Salvar alterações") não reagem a um simples .click().
  function realClick(el) {
    try { el.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = el.getBoundingClientRect();
    const o = {
      bubbles: true, cancelable: true, view: window, button: 0,
      clientX: r.left + r.width / 2, clientY: r.top + r.height / 2,
    };
    try { el.dispatchEvent(new PointerEvent("pointerdown", o)); } catch (e) {}
    el.dispatchEvent(new MouseEvent("mousedown", o));
    try { el.focus(); } catch (e) {}
    try { el.dispatchEvent(new PointerEvent("pointerup", o)); } catch (e) {}
    el.dispatchEvent(new MouseEvent("mouseup", o));
    if (typeof el.click === "function") el.click();
    else el.dispatchEvent(new MouseEvent("click", o));
  }

  // Acha o "Salvar alterações" OFICIAL da página (o do rodapé). Prefere a
  // correspondência exata e, entre elas, a ÚLTIMA no DOM (o botão de baixo) —
  // assim não clica num "Salvar" duplicado/oculto que apareça antes.
  function findSaveButton() {
    const all = clickables(document);
    let pool = all.filter((e) => clickableText(e) === "salvar alteracoes");
    if (!pool.length) pool = all.filter((e) => clickableText(e).includes("salvar alteracoes"));
    if (!pool.length) pool = all.filter((e) => {
      const t = clickableText(e);
      return t === "salvar" || t === "gravar" || t.includes("salvar");
    });
    const sized = pool.filter((e) => {
      const r = e.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    });
    const list = sized.length ? sized : pool;
    return list.length ? list[list.length - 1] : null;
  }

  async function saveZone() {
    // O botão pode demorar a aparecer logo após adicionar as entradas.
    const salvar = findSaveButton() || (await waitFor(findSaveButton, 5000));
    if (!salvar) {
      progress('Aviso: botão "Salvar alterações" não encontrado — salve manualmente.');
      return false;
    }
    // Espera habilitar (alguns painéis só liberam o "Salvar" após validar).
    await waitFor(() => !isDisabled(salvar), 4000);
    progress('Clicando em "' + (salvar.textContent || "").trim().slice(0, 30) + '"…');
    realClick(salvar);
    await wait(900);
    return true;
  }

  async function applyRecords(records, options) {
    options = options || {};
    const results = [];
    progress("Iniciando — " + records.length + " registro(s).");

    for (let i = 0; i < records.length; i++) {
      const rec = records[i];
      const label = (rec.name || "@") + " " + rec.type + " " + rec.value;
      progress("(" + (i + 1) + "/" + records.length + ") " + label);
      try {
        let entry = findEditableEntry();
        if (!entry) {
          const novo = findClickableByText([
            "nova entrada", "novo registro", "adicionar entrada", "adicionar registro",
            "novo apontamento", "nova linha", "add record", "new record", "adicionar",
          ]);
          if (novo) novo.click();
          entry = (await waitFor(() => findEditableEntry(), 6000)) || findAnyEntry();
        }
        if (!entry) {
          throw new Error('Formulário de "Nova entrada" não encontrado. Abra a Edição de Zona / Modo Avançado e use "Diagnosticar".');
        }

        // 1) TIPO primeiro — selecionar o tipo costuma re-renderizar a linha e
        //    LIMPAR o campo nome. Por isso o nome é preenchido por último.
        if (!selectType(entry.typeSelect, rec.type)) {
          throw new Error('Não consegui selecionar o tipo "' + rec.type + '".');
        }
        await wait(350);

        // 2) Re-localiza referências (o DOM pode ter sido recriado).
        entry = findEditableEntry() || entry;
        let c = entry.container;

        // 3) VALOR
        const valueField = valueFieldOf(c) || entry.valueField;
        if (valueField) fillInput(valueField, rec.value || "");
        else throw new Error("Campo de valor não encontrado.");
        await wait(150);

        // 4) Prioridade (MX / SRV)
        if (/^(mx|srv)$/i.test(rec.type) && rec.priority != null && rec.priority !== "") {
          const prField = findFieldByKeywords(c, PRIORITY_KEYS);
          if (prField) fillInput(prField, String(rec.priority));
        }

        // 5) NOME por último — re-localiza a linha já preenchida (com valor).
        const active = findFilledEntry() || entry;
        c = active.container;
        let nameField = findFieldByKeywords(c, NAME_KEYS);
        if (nameField) fillInput(nameField, rec.name || "");
        await wait(200);

        // 6) Garante o nome imediatamente antes de adicionar (se foi limpo).
        if (rec.name) {
          const cc = (findFilledEntry() || active).container;
          nameField = findFieldByKeywords(cc, NAME_KEYS);
          if (nameField && norm(nameField.value) !== norm(rec.name)) {
            fillInput(nameField, rec.name);
            await wait(150);
          }
          c = cc;
        }

        // 7) Confirmar a inclusão da entrada.
        const addBtn =
          findClickableByTextWithin(c, ["adicionar", "incluir", "confirmar", "ok", "salvar entrada", "add"]) ||
          findClickableByText(["adicionar", "incluir", "confirmar", "add record"]);
        if (addBtn) addBtn.click();
        await wait(700);

        results.push({ record: rec, ok: true });
        progress("   ✓ adicionado");
      } catch (e) {
        results.push({ record: rec, ok: false, error: e.message || String(e) });
        progress("   ✗ " + (e.message || e));
      }
    }

    // Salvar só quando o checkbox "Salvar automaticamente" estiver marcado.
    let saved = false;
    if (options.autoSave) {
      await wait(900); // deixa as entradas assentarem antes de salvar
      saved = await saveZone();
    } else {
      progress('Registros adicionados. Clique em "Salvar alterações" para gravar.');
    }

    return { items: results, saved: saved };
  }

  // ---------------------------------------------------------------------------
  // Diagnóstico
  // ---------------------------------------------------------------------------
  function detectDomain() {
    const q = new URLSearchParams(location.search);
    if (q.get("fqdn")) return q.get("fqdn");
    if (q.get("dominio")) return q.get("dominio");
    const m = location.href.match(/([a-z0-9-]+\.(?:com\.br|net\.br|org\.br|[a-z]{2,}\.br|br))(?:\/|$|\?|#)/i);
    if (m) return m[1];
    const heads = [...document.querySelectorAll('h1,h2,h3,[class*="domain"],[class*="dominio"]')]
      .map((e) => e.textContent).join(" ");
    const m2 = heads.match(/[a-z0-9-]+\.[a-z0-9.-]*br\b/i);
    return m2 ? m2[0] : null;
  }

  function diagnose() {
    const lines = [];
    lines.push("URL: " + location.href);
    lines.push("Domínio detectado: " + (detectDomain() || "(não detectado)"));
    lines.push("");

    const sels = typeSelects(document);
    lines.push("Selects de TIPO de registro: " + sels.length);
    sels.forEach((s, i) =>
      lines.push("  [" + i + "] opções: " + [...s.options].map((o) => o.textContent.trim()).filter(Boolean).join(", "))
    );
    lines.push("");

    const entry = findEditableEntry() || findAnyEntry();
    lines.push("Formulário de entrada editável: " + (entry ? "SIM" : "não"));
    if (entry) {
      lines.push("  nome:  " + (findFieldByKeywords(entry.container, NAME_KEYS) ? "ok" : "NÃO ACHOU"));
      lines.push("  valor: " + (valueFieldOf(entry.container) ? "ok" : "NÃO ACHOU"));
    }
    lines.push("");

    const btns = clickables(document);
    lines.push("Botões/links visíveis (" + btns.length + "):");
    btns.slice(0, 60).forEach((b) => {
      const t = (b.textContent || b.value || "").trim().replace(/\s+/g, " ");
      if (t) lines.push("  • " + t.slice(0, 60));
    });
    lines.push("");

    const inputs = [...document.querySelectorAll("input, select, textarea")].filter(isVisible);
    lines.push("Campos visíveis (" + inputs.length + "):");
    inputs.slice(0, 50).forEach((f) =>
      lines.push(
        "  - <" + f.tagName.toLowerCase() + (f.type ? " type=" + f.type : "") +
        '> rótulo="' + labelText(f).slice(0, 48) + '"'
      )
    );

    return lines.join("\n");
  }

  // ---------------------------------------------------------------------------
  // Canal de mensagens com o popup
  // ---------------------------------------------------------------------------
  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    (async () => {
      try {
        if (!msg || !msg.type) return sendResponse({ ok: false, error: "mensagem inválida" });
        if (msg.type === "PING") {
          return sendResponse({ ok: true, url: location.href, domain: detectDomain() });
        }
        if (msg.type === "DIAGNOSE") {
          return sendResponse({ ok: true, report: diagnose() });
        }
        if (msg.type === "PREPARE_EDITOR") {
          const ready = await prepareEditor();
          return sendResponse({ ok: true, ready: ready });
        }
        if (msg.type === "SAVE_ZONE") {
          const saved = await saveZone();
          return sendResponse({ ok: true, saved: saved });
        }
        if (msg.type === "APPLY_RECORDS") {
          const res = await applyRecords(msg.records || [], msg.options || {});
          return sendResponse({ ok: true, result: res });
        }
        return sendResponse({ ok: false, error: "tipo desconhecido: " + msg.type });
      } catch (e) {
        return sendResponse({ ok: false, error: (e && e.message) || String(e) });
      }
    })();
    return true; // resposta assíncrona
  });

  console.log("Registro.br DNS Helper: content script ativo.");
})();
