// Browser check for the data tab's query bar (filters, joins, grouping), the configurable
// foreign-key navigation (query tab / data tab / split) and the referencing-row expansion.
// Needs a running server (BASE_URL defaults to :5005) whose demo connection holds the little
// shop: customers(id, name, city) and orders(id, customer_id → customers.id, amount, state),
// three customers, five orders of which three are open.
import { chromium } from "playwright";

const baseUrl = process.env.BASE_URL ?? "http://localhost:5005";
const errors = [];
const expected = /status of (400|404)/;

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1600, height: 950 } });
const page = await context.newPage();
page.on("console", m => {
  if (m.type() === "error" && !expected.test(m.text())) errors.push(m.text());
});
page.on("pageerror", e => errors.push(String(e)));

const fail = async (label, error) => {
  await page.screenshot({ path: `smoke-browse-${label}.png` });
  console.error("console errors:", errors.slice(0, 5).join(" | ") || "(none)");
  console.error("body:", (await page.locator("body").innerText()).slice(0, 600));
  throw error;
};

const node = (label) =>
  page.locator(".mantine-UnstyledButton-root").filter({ hasText: label }).first();

// A clean slate: no remembered tabs, and navigation back on its default.
await page.request.put(`${baseUrl}/api/workspace/tabs`, { data: [] });
await page.request.put(`${baseUrl}/api/workspace/item/fk-nav-mode`, {
  headers: { "content-type": "application/json" }, data: JSON.stringify("query"),
});
await page.goto(baseUrl, { waitUntil: "networkidle" });
await page.getByText("DEMO", { exact: true }).waitFor({ timeout: 15000 });

// --- open the orders table -----------------------------------------------------------
await node(/^DEMO$/).click();
await page.getByText("Tables", { exact: true }).click();
const orders = node(/^orders$/);
await orders.waitFor({ timeout: 15000 });
await orders.dblclick();
await page.getByRole("button", { name: "Save", exact: true }).waitFor({ timeout: 15000 });
await page.getByText(/5 rows/).waitFor({ timeout: 15000 });

// --- the query bar: several filters together ------------------------------------------
try {
  await page.getByRole("button", { name: "Filter" }).click();
  await page.getByRole("combobox", { name: "Column", exact: true }).click();
  await page.getByRole("option", { name: "state" }).click();
  await page.getByRole("combobox", { name: "Operator", exact: true }).click();
  await page.getByRole("option", { name: "=", exact: true }).click();
  await page.getByLabel("Value").fill("open");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await page.getByText(/3 rows/).waitFor({ timeout: 10000 });

  await page.getByRole("button", { name: "Filter" }).click();
  await page.getByRole("combobox", { name: "Column", exact: true }).click();
  await page.getByRole("option", { name: "amount" }).click();
  await page.getByRole("combobox", { name: "Operator", exact: true }).click();
  await page.getByRole("option", { name: "≥" }).click();
  await page.getByLabel("Value").fill("10");
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await page.getByText(/2 rows/).waitFor({ timeout: 10000 });

  // The chips are the way back.
  await page.getByLabel("Remove state = open").click();
  await page.getByLabel(/Remove amount/).click();
  await page.getByText(/5 rows/).waitFor({ timeout: 10000 });
} catch (e) { await fail("filters", e); }

// --- a join shows the referenced columns in the same grid ------------------------------
try {
  await page.getByRole("button", { name: /^Join/ }).click();
  await page.getByRole("button", { name: /customer_id → customers/ }).click();
  await page.keyboard.press("Escape");
  await page.getByText("customers.name", { exact: true }).waitFor({ timeout: 10000 });
  await page.getByText(/joined view cannot be edited/).waitFor({ timeout: 10000 });

  // Grouping by the joined column, summed: linus has 50, ada 30.
  await page.getByRole("button", { name: /^Group/ }).click();
  await page.getByRole("combobox", { name: "Group by", exact: true }).click();
  await page.getByRole("option", { name: "customers.name" }).click();
  await page.keyboard.press("Escape");
  await page.getByText("count(*)", { exact: true }).waitFor({ timeout: 10000 });
  await page.getByText(/grouped view cannot be edited/).waitFor({ timeout: 10000 });

  await page.getByRole("button", { name: "Clear all" }).click();
  await page.getByText(/5 rows/).waitFor({ timeout: 10000 });
} catch (e) { await fail("join-group", e); }

// --- following a foreign key, in each of its three modes -------------------------------
try {
  // The default: a query tab with the SELECT.
  await page.getByLabel("Follow foreign key").first().click();
  await page.getByText("DEMO · query").first().waitFor({ timeout: 10000 });

  // Back on the data tab, switch to split view.
  await node(/^orders$/).dblclick();
  await page.getByRole("button", { name: "Save", exact: true }).waitFor({ timeout: 15000 });
  await page.getByLabel("Foreign-key navigation").click();
  await page.getByText("in a split view").click();

  // Following now opens the referenced customer next to the orders, filtered to the row.
  await page.getByLabel("Follow foreign key").first().click();
  await page.getByLabel("Remove id = 1").waitFor({ timeout: 15000 });
  await page.getByText("ada", { exact: true }).waitFor({ timeout: 10000 });
} catch (e) { await fail("follow", e); }

// --- referencing rows: expanded inline, and opened as a further split -------------------
try {
  // The split customer tab has one incoming key, so the expander toggles it directly.
  await page.getByLabel("Expand referencing rows").first().click();
  await page.getByText(/orders · customer_id = 1/).waitFor({ timeout: 10000 });
  await page.getByText(/2 row\(s\)/).waitFor({ timeout: 10000 });

  // And its open button opens the referencing table like a followed key — another split.
  await page.getByLabel("Open orders").click();
  await page.getByLabel("Remove customer_id = 1").waitFor({ timeout: 15000 });
} catch (e) { await fail("referencing", e); }

await page.screenshot({ path: "smoke-browse.png" });
await browser.close();

if (errors.length > 0) {
  console.error("console errors:\n" + errors.join("\n"));
  process.exit(1);
}
console.log("smoke-browse ok");
