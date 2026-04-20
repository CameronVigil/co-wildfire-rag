import './styles/main.css';
import { initMap } from './map.js';
import { initSidebar, bindSendButton, openWithCell, showLoading, showError, renderAnswer } from './sidebar.js';
import { postQuery } from './api.js';

// Initialize sidebar DOM bindings
initSidebar();

// Initialize map — passes cell-click callback
initMap('map', {
  onCellClick: (props) => openWithCell(props),
});

// Wire up the RAG send button
bindSendButton(async (question, h3Index, location) => {
  showLoading(true);
  try {
    const data = await postQuery(question, h3Index, location);
    renderAnswer(data);
  } catch (err) {
    showError(err.message ?? 'Failed to get a response. Is the API running?');
  }
});
