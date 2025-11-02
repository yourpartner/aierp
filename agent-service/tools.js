import axios from "axios";

export function createCtx() {
  return {
    http: axios.create(),
    base: process.env.BACKEND_BASE,
    company: process.env.COMPANY_CODE
  };
}

export const toolSpecs = [
  {
    name: "getSchema",
    description: "获取实体激活schema与查询白名单",
    parameters: {
      type: "object",
      properties: { name: { type: "string" } },
      required: ["name"]
    }
  },
  {
    name: "search",
    description: "DSL 搜索：where/orderBy/page/pageSize",
    parameters: {
      type: "object",
      properties: {
        entity: { type: "string" },
        dsl: { type: "object" }
      },
      required: ["entity", "dsl"]
    }
  },
  {
    name: "getDetail",
    description: "读取详情",
    parameters: {
      type: "object",
      properties: { entity: { type: "string" }, id: { type: "string" } },
      required: ["entity", "id"]
    }
  },
  {
    name: "save",
    description: "保存对象（凭证自动编号与借贷校验在后端完成）",
    parameters: {
      type: "object",
      properties: { entity: { type: "string" }, payload: { type: "object" } },
      required: ["entity", "payload"]
    }
  },
  {
    name: "resolveEmployeeByName",
    description: "按姓名模糊查员工并返回候选编码",
    parameters: {
      type: "object",
      properties: { name: { type: "string" } },
      required: ["name"]
    }
  },
  {
    name: "signBlobSAS",
    description: "为附件生成只读SAS",
    parameters: {
      type: "object",
      properties: { blobName: { type: "string" } },
      required: ["blobName"]
    }
  }
];

export async function executeTool(name, args, ctx) {
  switch (name) {
    case "getSchema": {
      const r = await ctx.http.get(`${ctx.base}/schemas/${args.name}`);
      return r.data;
    }
    case "search": {
      const r = await ctx.http.post(`${ctx.base}/objects/${args.entity}/search`, args.dsl, {
        headers: { "x-company-code": ctx.company }
      });
      return r.data;
    }
    case "getDetail": {
      const r = await ctx.http.get(`${ctx.base}/objects/${args.entity}/${args.id}`, {
        headers: { "x-company-code": ctx.company }
      });
      return r.data;
    }
    case "save": {
      const r = await ctx.http.post(`${ctx.base}/objects/${args.entity}`, { payload: args.payload }, {
        headers: { "x-company-code": ctx.company }
      });
      return r.data;
    }
    case "resolveEmployeeByName": {
      const dsl = { where: [{ json: "name", op: "eq", value: args.name }], page: 1, pageSize: 10 };
      const r = await ctx.http.post(`${ctx.base}/objects/employee/search`, dsl, {
        headers: { "x-company-code": ctx.company }
      });
      return r.data;
    }
    case "signBlobSAS": {
      const r = await ctx.http.post(`${ctx.base}/attachments/sas`, { blobName: args.blobName });
      return r.data;
    }
    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}
