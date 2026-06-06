"""Extract all unique recipients from sent emails in personal Gmail account.

Fetches only the To/Cc headers (metadata format) for speed.
Uses Gmail API batch requests to process 50 messages per API call.
Outputs a JSON file with unique contacts: {email, name}.
"""

import json
import re
import sys
import os

sys.path.insert(0, os.path.dirname(__file__))

from src.auth import load_credentials
from googleapiclient.discovery import build
from googleapiclient.http import BatchHttpRequest

ACCOUNT = "personal"
OUTPUT_FILE = "sent_contacts.json"


def parse_email_addresses(header_value):
    """Parse email addresses from a To/Cc header value.

    Handles formats like:
      - "Name <email@example.com>"
      - "email@example.com"
      - "Name <email@example.com>, Other <other@example.com>"
    """
    if not header_value:
        return []

    contacts = []
    # Split on commas, but not commas inside quotes
    parts = re.split(r',(?=(?:[^"]*"[^"]*")*[^"]*$)', header_value)

    for part in parts:
        part = part.strip()
        if not part:
            continue

        # Try "Name <email>" format
        match = re.match(r'^"?([^"<]*?)"?\s*<([^>]+)>', part)
        if match:
            name = match.group(1).strip().strip('"')
            email = match.group(2).strip().lower()
            contacts.append({"name": name, "email": email})
        else:
            # Plain email
            email = part.strip().strip("<>").lower()
            if "@" in email:
                contacts.append({"name": "", "email": email})

    return contacts


def main():
    creds = load_credentials(ACCOUNT)
    service = build("gmail", "v1", credentials=creds)

    # Step 1: Get all sent message IDs
    print("[1/3] Fetching sent message IDs...")
    all_ids = []
    page_token = None
    while True:
        kwargs = {"userId": "me", "labelIds": ["SENT"], "maxResults": 500}
        if page_token:
            kwargs["pageToken"] = page_token
        results = service.users().messages().list(**kwargs).execute()
        messages = results.get("messages", [])
        all_ids.extend([m["id"] for m in messages])
        page_token = results.get("nextPageToken")
        if not page_token:
            break
        print(f"  ... {len(all_ids)} IDs so far")

    print(f"  Total sent messages: {len(all_ids)}")

    # Step 2: Fetch To/Cc headers in batches of 50
    print("[2/3] Fetching recipient headers (batch mode)...")
    all_contacts = {}  # email -> name
    processed = 0
    batch_size = 50

    for i in range(0, len(all_ids), batch_size):
        batch_ids = all_ids[i:i + batch_size]
        batch = service.new_batch_http_request()

        def make_callback(msg_id):
            def callback(request_id, response, exception):
                if exception:
                    return
                headers = response.get("payload", {}).get("headers", [])
                for h in headers:
                    name = h.get("name", "").lower()
                    if name in ("to", "cc", "bcc"):
                        for contact in parse_email_addresses(h.get("value", "")):
                            email = contact["email"]
                            # Keep the first non-empty name we find
                            if email not in all_contacts or (contact["name"] and not all_contacts[email]):
                                all_contacts[email] = contact["name"]
            return callback

        for msg_id in batch_ids:
            batch.add(
                service.users().messages().get(
                    userId="me",
                    id=msg_id,
                    format="metadata",
                    metadataHeaders=["To", "Cc", "Bcc"],
                ),
                callback=make_callback(msg_id),
            )

        batch.execute()
        processed += len(batch_ids)
        if processed % 500 == 0 or processed == len(all_ids):
            print(f"  ... {processed}/{len(all_ids)} messages processed, {len(all_contacts)} unique contacts")

    # Step 3: Filter out own email and noisy addresses
    print("[3/3] Filtering results...")
    skip_patterns = [
        "noreply", "no-reply", "notifications@", "mailer-daemon",
        "donotreply", "do-not-reply", "unsubscribe",
    ]
    # Your own address(es) to exclude from the contact list, comma-separated.
    own_emails = {
        e.strip().lower()
        for e in os.environ.get("CC_GMAIL_OWN_EMAILS", "").split(",")
        if e.strip()
    }

    filtered = []
    for email, name in sorted(all_contacts.items()):
        if email in own_emails:
            continue
        if any(p in email for p in skip_patterns):
            continue
        filtered.append({"email": email, "name": name})

    # Write output
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(filtered, f, indent=2, ensure_ascii=False)

    print(f"\nDone! {len(filtered)} unique contacts saved to {OUTPUT_FILE}")
    for c in filtered[:10]:
        name_part = f" ({c['name']})" if c['name'] else ""
        print(f"  {c['email']}{name_part}")
    if len(filtered) > 10:
        print(f"  ... and {len(filtered) - 10} more")


if __name__ == "__main__":
    main()
