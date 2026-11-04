/** Largest body read from a llama-server: 1000 rerank results of 200 probabilities fit well inside it. */
export const MAX_UPSTREAM_BYTES = 64 * 1024 * 1024;

export class ResponseTooLarge extends Error {}

/** Reads a response as text, giving up as soon as it passes `limit` bytes. */
export async function readBounded(response: Response, limit: number = MAX_UPSTREAM_BYTES): Promise<string> {
  if (!response.body) return "";
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let total = 0;
  let text = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return text + decoder.decode();
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel().catch(() => {});
      throw new ResponseTooLarge();
    }
    text += decoder.decode(value, { stream: true });
  }
}
