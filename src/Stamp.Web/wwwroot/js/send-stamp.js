// The public page's send flow.
//
// 1. Stripe Elements collects the card in "deferred intent" mode (amount, currency and
//    captureMethod: 'manual' known up front), so nothing is created until the sender submits.
// 2. On submit we validate the card fields, then POST the message; the server saves it as
//    awaiting payment and creates the manual-capture PaymentIntent, returning its client secret.
// 3. stripe.confirmPayment authorizes the card (3-D Secure included) and redirects to the
//    return URL, where the server confirms the hold and delivers the message.
//
// In development with Payments:Provider=Fake there's no card form: step 3 calls a dev endpoint
// that simulates a successful authorization instead.
(() => {
  const form = document.getElementById('stamp-form');
  if (!form) return;

  const provider = form.dataset.provider;
  const button = form.querySelector('button[type="submit"]');
  const errorBox = document.getElementById('stamp-error');
  const antiforgery = form.querySelector('input[name="__RequestVerificationToken"]').value;
  const fields = ['senderName', 'senderEmail', 'subject', 'body'];

  let stripe = null;
  let elements = null;

  if (provider === 'Stripe') {
    if (typeof window.Stripe !== 'function') {
      showError('The secure card form could not load. Check your connection or disable content blockers, then reload.');
      button.disabled = true;
      return;
    }

    stripe = window.Stripe(form.dataset.publishableKey);
    elements = stripe.elements({
      mode: 'payment',
      amount: Number(form.dataset.amount),
      currency: form.dataset.currency,
      captureMethod: 'manual',
      paymentMethodTypes: ['card'],
      appearance: {
        theme: 'stripe',
        variables: { colorPrimary: '#b4441f', borderRadius: '8px', fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif' },
      },
    });
    const container = document.getElementById('payment-element');
    container.textContent = '';
    elements.create('payment').mount(container);
  }

  // Reuse the same message and payment if the sender retries (e.g. after a declined card)
  // without editing the form, instead of creating a new draft each time.
  let pending = null;

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    clearErrors();

    const message = Object.fromEntries(fields.map((name) => [name, form.elements[name].value.trim()]));
    const missing = fields.find((name) => !message[name]);
    if (missing) {
      fieldError(missing, 'This field is required.');
      form.elements[missing].focus();
      return;
    }

    setBusy(true);
    try {
      if (elements) {
        const { error } = await elements.submit();
        if (error) {
          showError(error.message);
          return;
        }
      }

      const fingerprint = JSON.stringify(message);
      if (!pending || pending.fingerprint !== fingerprint) {
        const response = await fetch(form.dataset.endpoint, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', RequestVerificationToken: antiforgery },
          body: fingerprint,
        });
        const data = await response.json().catch(() => ({}));
        if (!response.ok) {
          if (data.field) fieldError(data.field, data.message);
          else showError(data.message || 'Something went wrong. Please try again.');
          return;
        }
        pending = { fingerprint, ...data };
      }

      if (stripe) {
        const { error } = await stripe.confirmPayment({
          elements,
          clientSecret: pending.clientSecret,
          confirmParams: {
            return_url: pending.returnUrl,
            payment_method_data: { billing_details: { name: message.senderName, email: message.senderEmail } },
          },
        });
        // Only reached when the card step fails right away (declined, incomplete, ...).
        showError(error.message);
      } else {
        const response = await fetch(`/dev/payments/${encodeURIComponent(pending.paymentId)}/authorize`, {
          method: 'POST',
          headers: { RequestVerificationToken: antiforgery },
        });
        if (!response.ok) {
          showError('The simulated card authorization failed.');
          return;
        }
        window.location.assign(pending.returnUrl);
      }
    } catch {
      showError('We couldn\'t reach Stamp. Check your connection and try again.');
    } finally {
      setBusy(false);
    }
  });

  function setBusy(busy) {
    button.disabled = busy;
    button.textContent = busy ? 'Sending…' : button.dataset.idleLabel;
  }

  function clearErrors() {
    errorBox.hidden = true;
    errorBox.textContent = '';
    form.querySelectorAll('[data-error-for]').forEach((element) => { element.textContent = ''; });
  }

  function showError(text) {
    errorBox.textContent = text;
    errorBox.hidden = false;
  }

  function fieldError(name, text) {
    const target = form.querySelector(`[data-error-for="${name}"]`);
    if (target) target.textContent = text;
    else showError(text);
  }
})();
