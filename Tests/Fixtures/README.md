# Cart quantity regression images

`quantity-dark-305.png` and `quantity-dark-273.png` are unscaled, dialog-only crops
from the authorized live Cart validation on 2026-09-25. They contain the offered
Mastela Fruit counts 305 and 273, respectively. They contain no credentials,
account identity, character identity, or surrounding desktop content.

The game rendered black digits over a blue selection. The old reconstruction
assumed white digits, so it erased this text before numeric recognition. Tests
embed these exact crop pixels into full client-sized frames at two dimensions
and require the actual observed counts. The fixtures do not substitute text or
provide expected counts to OCR. Synthetic tests separately retain white-text
coverage and reject blank, mixed-polarity, negative, and out-of-range values.

These images establish recognition of the observed dialogs. They do not establish
that capacity-limited live transfers or arbitrary future skins work.
