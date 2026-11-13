# Local and cloud chat

LocalMind starts with cloud chat disabled. Local conversations use your installed
Ollama models. A cloud provider is optional and uses your own API account.
ChatGPT, Claude and Gemini consumer subscriptions do not necessarily include API
credit. Check the provider's API billing page before connecting a key.

## Connect a provider

1. Open **Settings → Cloud models** and allow cloud chat.
2. Choose OpenAI, Anthropic or Google Gemini and enter an API key.
3. Load the available models or enter an exact model identifier. Check access and
   save the selection. This access check does not generate a paid answer.
4. If the provider's metadata does not report image capability, confirm image
   support from that model's documentation before enabling it. Image input and
   image generation are separate capabilities.
5. Select the cloud model in the chat composer. Before its first request, review
   the prepared messages, included data categories and destination.

Keys are stored in the operating system credential store for the current
LocalMind profile. A locked or unavailable credential store produces an error;
there is no plaintext file fallback. Disconnecting a provider revokes its chat
approval and removes its key when the credential store is available.

Approval applies to a provider, model and data categories. Expanding those
categories or changing the destination requires approval again. Turning cloud
chat off revokes existing approvals. Web search and connected apps have their
own controls; disabling cloud chat does not make an enabled web search local.

The preview shows context prepared at that moment. Search results and local
documents can change before the send. LocalMind checks the approved categories
again at dispatch. A request can include conversation history, relevant memory,
project or document excerpts and images. Review those categories carefully.

## Costs and interrupted requests

Set input and output prices for the selected model using the provider's current
pricing page. Prices are user-supplied estimates, not an account billing feed.
Changing a model clears its old prices. Before a request, LocalMind reserves a
conservative estimated cost. It reconciles the estimate when usage arrives.

A local monthly limit blocks requests that cannot fit within the remaining
estimated budget. Unknown pricing blocks a budgeted request. Image estimates
include the encoded image bytes and can be much higher than the eventual cost.
Provider-specific discounts, tiers, extra fees and spending from other apps or
devices are not known to LocalMind. Set a provider-side limit as well if your
account offers one.

An interrupted request might already have been billed. LocalMind preserves its
reservation until it has usable usage evidence and does not automatically repeat
paid generation. Retrying is an explicit new request and may incur another charge.
Partial replies and drafts are preserved. Failed or cancelled turns are excluded
from the model's completed conversation history.

## Screenshots

Paste an image with Ctrl+V or Cmd+V, choose files with the attachment button, or
drop them into the composer. Text paste still works. Each message accepts up to
four still PNG, JPEG or WebP images, each at most 8 MiB and 16 million pixels.

The service decodes each image, applies its orientation and removes metadata.
Screenshots are stored losslessly as PNG. If the normalized image is too large,
crop it and try again. Images stay in the current profile and are tied to their
draft and conversation. Deleting a conversation deletes its saved image data.
Unsent images expire after 48 hours and are cleaned up during later uploads.

A text-only model cannot interpret a screenshot. LocalMind asks you to choose a
compatible model instead of silently switching to a cloud provider. A cloud
request containing images requires attachment consent, including images from
earlier messages in that conversation.

## Documents and OCR

PDF, TXT, Markdown and CSV files can also be attached to a conversation. PDFs are
limited to 30 pages, with up to 4,000 extracted characters per page. Text files
use UTF-8 and contribute up to 12,000 characters. Open the attachment to inspect
the extraction. A response's document-excerpt disclosure shows the text actually
included after context budgeting. Page images, formatting and blank or scanned
pages are not interpreted by text extraction.

**Attachment reading → OCR text only** uses an installed Tesseract executable.
This mode reads text; it does not understand objects, charts or layout. LocalMind
shows an error when Tesseract is missing instead of treating the image as read.
Choose a vision-capable model for visual interpretation.

See [Visual workspace](visual-workspace.md) for editable diagrams, saved image
references, charts and learning widgets.

## Developer verification

Backend tests cover provider stream fixtures, key isolation, consent revocation,
concurrent budget reservations, interrupted writes, image decoding and ownership.
Frontend tests cover draft ownership, paste/drop, provider selection, disclosure
and stream recovery. These tests do not establish live provider compatibility or
native clipboard behavior on every platform.

The paid provider smoke test is opt-in. Read
`services/ai/tests/test_provider_live.py` before running it. Use a dedicated test
profile and account budget. Do not add keys or generated conversation reports to
the repository.

The smoke test also requires `LOCALMIND_PROVIDER_LIVE_BUDGET_USD` (greater than zero,
at most 1), configured token prices and an estimate within that limit. It performs one
short synthetic generation without automatic retries. See the [contract fixtures](../contracts/README.md)
for the offline compatibility matrix and fixture update procedure.

The comparison harness in `scripts/evaluation/compare_experience.py` prepares a
blinded review from synthetic tasks. Run `--help` for the preparation, execution
and scoring commands. Human reviews and model runs are separate steps; preparing
the review file does not produce a quality score.
