import { PbxConnectorConfig } from "../../shared/pbx";
import { FreePbxAdapter } from "./FreePbxAdapter";
import { PbxAdapter } from "./PbxAdapter";
import { YeastarS100Adapter } from "./YeastarS100Adapter";

export function createPbxAdapter(config: PbxConnectorConfig): PbxAdapter {
  if (config.vendor === "yeastar-s100") {
    return new YeastarS100Adapter(config);
  }

  return new FreePbxAdapter(config);
}
