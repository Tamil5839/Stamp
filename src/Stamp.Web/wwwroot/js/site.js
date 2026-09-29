// Progressive enhancements: character counters, live countdowns and confirm prompts.
// Every page works without JavaScript; this only makes it nicer.
(() => {
  document.querySelectorAll('[data-counter]').forEach((field) => {
    const hint = document.querySelector(`[data-counter-for="${field.id}"]`);
    if (!hint) return;
    const max = Number(field.getAttribute('maxlength')) || null;
    const min = Number(field.dataset.min) || 0;
    const update = () => {
      const length = field.value.length;
      let text = max ? `${length.toLocaleString()} / ${max.toLocaleString()}` : `${length.toLocaleString()} characters`;
      if (min && length < min) text += ` · at least ${min} characters`;
      hint.textContent = text;
      hint.classList.toggle('is-over', max !== null && length > max);
    };
    field.addEventListener('input', update);
    update();
  });

  const countdowns = document.querySelectorAll('[data-countdown]');
  const format = (ms) => {
    if (ms <= 0) return 'Expiring';
    const minutes = Math.ceil(ms / 60000);
    const days = Math.floor(minutes / 1440);
    const hours = Math.floor((minutes % 1440) / 60);
    if (days >= 1) return `${days}d ${hours}h left`;
    if (hours >= 1) return `${hours}h ${minutes % 60}m left`;
    return `${minutes}m left`;
  };
  const tick = () => countdowns.forEach((element) => {
    const remaining = new Date(element.getAttribute('datetime')).getTime() - Date.now();
    element.textContent = format(remaining);
    element.classList.toggle('is-urgent', remaining < 24 * 60 * 60 * 1000);
  });
  if (countdowns.length) {
    tick();
    setInterval(tick, 30000);
  }

  document.querySelectorAll('[data-confirm]').forEach((button) => {
    button.form?.addEventListener('submit', (event) => {
      if (event.submitter === button && !window.confirm(button.dataset.confirm)) {
        event.preventDefault();
      }
    });
  });
})();
