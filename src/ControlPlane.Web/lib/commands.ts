export const commandGroup = (method: string) => method.startsWith("software.office_") ? "Office"
  : method.startsWith("network.ibsng_") ? "IBSng" : "Endpoint";
