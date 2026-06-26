// Service worker mínimo (MV3). Toda a lógica vive no content script e no popup.
chrome.runtime.onInstalled.addListener(() => {
  console.log("Registro.br DNS Helper instalado/atualizado.");
});
