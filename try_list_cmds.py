"""One-off: try employee-list command variants on 9922. Usage: python try_list_cmds.py [device_ip]"""
import sys
import time
from fetch_data import hdcp_call, IP as DEFAULT_IP

IP = sys.argv[1].strip() if len(sys.argv) > 1 else DEFAULT_IP

cmds = [
    'GetEmployeeIndex(index="0")',
    'GetEmployeeIndex(index="1")',
    'GetEmployeeByIndex(index="0")',
    'GetEmployeeNum()',
    'GetEmployeeCount()',
    'QueryEmployee(start="0" count="50")',
    'GetEmployeeList(start="0" count="50")',
    'GetAllEmployee(start="0" count="50")',
    'DownloadEmployee()',
    'ExportEmployee()',
    'GetUserList()',
    'GetEmployee(employee_id="25258")',
]
for c in cmds:
    try:
        r = hdcp_call(IP, c, timeout=15)
        tag = "OK" if 'success' in r and "Not Support" not in r and "unknown id" not in r else "..."
        print(tag, c[:55])
        print("   ", r[:150].replace("\n", " "))
    except Exception as e:
        print("ERR", c, e)
    time.sleep(0.4)
