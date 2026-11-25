import time
from fetch_data import hdcp_call, IP

cmds = [
    "GetAllUser()",
    "GetAllUsers()",
    "DownloadAllEmployee()",
    "SyncEmployee()",
    'GetEmployeeInfo(start="0" count="500")',
    "GetEmployee(employee_id=\"\")",
    "ExportUser()",
    "GetEnrolledUser()",
    "QueryAllUser()",
    "GetEmployeeByIndex(index=\"0\")",
    "GetEmployeeIndex(index=\"0\")",
]
for c in cmds:
    try:
        r = hdcp_call(IP, c, timeout=12)
        print(c[:55], "->", r[:120].replace("\n", " "))
    except Exception as e:
        print(c, e)
    time.sleep(0.35)
