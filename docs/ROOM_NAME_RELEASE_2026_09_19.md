# Room-name identification release

## Behavior

- Room name is the required staff-facing identifier; names are checked without case sensitivity and protected by unique indexes.
- Room number, size, and description are optional on creation. Empty descriptions and sizes can be explicitly cleared on update.
- Dashboard room lists, booking selectors, check-in/out, maintenance, search, and room details show names.
- Dashboard and guest size displays are disabled in `config/roomPresentation.ts`. Number entry is also disabled. These are build-time settings, not an admin toggle.
- Existing descriptions, photos, prices, availability, and room IDs are retained. Empty descriptions do not produce invented descriptive text.
- Visit records receive a room-name snapshot and room ID in API responses so historic activity retains an identifiable room label.

## Migration and deployment order

1. Verify the live database is project `azclpxuabsffjkuhqzga` and take a pre-deployment backup of rooms and visit records.
2. Check for empty names and duplicate `lower(btrim("Name"))` values. Resolve any duplicates before migration; do not silently rename rooms.
3. Apply EF migration `20260919075402_OptionalRoomDetails` and its EF history entry. It backfills visit-record names, clears only rooms.RoomNumber and rooms.Size, and permits multiple blank room numbers.
4. Deploy the API before the dashboard. The dashboard needs the new optional-field behavior and visit-record name fields.
5. Deploy dashboard and guest website.
6. Confirm the existing room appears by name, images/rate are unchanged, room creation accepts omitted number/size/description, descriptions can be cleared, and public availability still works.

Cleared numbers and sizes require a pre-deployment backup if they must be recovered. The migration deliberately blocks rollback on populated rooms with blank numbers rather than inventing replacement identifiers or silently losing data.

## Validation

- Backend unit tests: 21 passed.
- Full integration suite: 296 passed against isolated local PostgreSQL, including room creation without optional fields and clearing a description.
- Dashboard and guest TypeScript checks and 12 tests each passed.
- Both production frontend builds passed; guest sitemap verified.
- Final targeted integration verification includes duplicate case-insensitive room-name rejection.

## Release status

Source changes prepared. Production migration, deployment, and live verification are pending access to the Moore Hotels Supabase account. The currently connected Supabase account lists other projects, not the live project. Do not deploy the API until its migration has been applied.
