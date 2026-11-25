import time
from fetch_data import hdcp_call, IP

cmds = [
    'GetEmployee(employee_id="100")',
    'GetRecord(start_time="2020-01-01 00:00:00" end_time="2026-12-31 23:59:59")',
    "GetEmployeePage(page=\"1\")",
    "GetEmployeeByPage(page=\"1\" size=\"50\")",
    "ExportEmployee()",
    "GetUser(user_id=\"100\")",
]
for c in cmds:
    print(c, "->", hdcp_call(IP, c)[:300])
    time.sleep(0.3)
