import type { ConversationMessage } from "../hooks/useConversations";
import type { Message } from "../types";

/** Decode saved responses before they enter the transcript. */
export function conversationMessages(value: unknown): Message[] {
  if (!Array.isArray(value)) throw new Error("Saved messages must be an array.");
  return value.map((item: unknown) => {
    if (!item || typeof item !== "object") throw new Error("Invalid saved message.");
    const row = item as ConversationMessage;
    if (typeof row.id !== "string" || typeof row.content !== "string" ||
        !["user", "assistant"].includes(row.role) || typeof row.conversation_id !== "string") {
      throw new Error("Saved message contract changed: id, role, content or conversation_id is invalid.");
    }
    if (row.attachments !== undefined && (!Array.isArray(row.attachments) || row.attachments.some(attachment =>
      !attachment || typeof attachment.id !== "string" || typeof attachment.filename !== "string" ||
      typeof attachment.mime_type !== "string" || typeof attachment.bytes !== "number"))) {
      throw new Error("Saved attachment contract changed.");
    }
    for (const key of ["error", "cancelled", "partial"] as const) {
      if (row[key] !== undefined && typeof row[key] !== "boolean") throw new Error(`Saved message ${key} must be boolean.`);
    }
    return { ...row, thinking: row.thinking ?? undefined, isThinking: false,
      errorMessage: row.error_message ?? undefined, attachmentDraftId: row.attachment_draft_id ?? undefined };
  });
}
