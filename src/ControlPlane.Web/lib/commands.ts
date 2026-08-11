export const commandGroup = (method: string) => method.startsWith("software.office_") ? "Office"
  : method.startsWith("system.windows_") ? "Windows"
  : method.startsWith("network.ibsng_") ? "IBSng" : "Endpoint";
