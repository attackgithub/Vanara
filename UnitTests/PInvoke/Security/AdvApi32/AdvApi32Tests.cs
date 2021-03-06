﻿using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Vanara.Extensions;
using Vanara.InteropServices;
using static Vanara.PInvoke.AdvApi32;
using static Vanara.PInvoke.Kernel32;

namespace Vanara.PInvoke.Tests
{
	[TestFixture()]
	public class AdvApi32Tests
	{
		internal const string fn = @"C:\Temp\help.ico";

		internal static object[] GetAuthCasesFromFile(bool validUser, bool validCred) => AuthCasesFromFile.Where(objs => ((object[])objs)[0].Equals(validUser) && ((object[])objs)[1].Equals(validCred)).ToArray();

		internal static object[] AuthCasesFromFile
		{
			get
			{
				const string authfn = @"C:\Temp\AuthTestCases.txt";
				var lines = File.ReadAllLines(authfn).Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
				var ret = new object[lines.Length];
				for (var i = 0; i < lines.Length; i++)
				{
					var items = lines[i].Split('\t').Select(s => s == string.Empty ? null : s).Cast<object>().ToArray();
					if (items.Length < 9) continue;
					bool.TryParse(items[0].ToString(), out var validUser);
					items[0] = validUser;
					bool.TryParse(items[1].ToString(), out var validCred);
					items[1] = validCred;
					ret[i] = items;
				}
				return ret;
			}
		}

		[Test]
		public void AccessCheckTest()
		{
			using (var pSD = GetSD(fn, SECURITY_INFORMATION.DACL_SECURITY_INFORMATION | SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION | SECURITY_INFORMATION.GROUP_SECURITY_INFORMATION))
			using (var hTok = SafeHTOKEN.FromProcess(GetCurrentProcess(), TokenAccess.TOKEN_IMPERSONATE | TokenAccess.TOKEN_DUPLICATE | TokenAccess.TOKEN_READ).Duplicate(SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation))
			{
				var ps = PRIVILEGE_SET.InitializeWithCapacity(10);
				var psSz = ps.SizeInBytes;
				var gm = GENERIC_MAPPING.GenericFileMapping;
				var accessMask = (uint)Kernel32.FileAccess.GENERIC_READ;
				MapGenericMask(ref accessMask, gm);
				var b = AccessCheck(pSD, hTok, accessMask, gm, ref ps, ref psSz, out var access, out var status);
				if (!b) TestContext.WriteLine($"AccessCheck failed: {Win32Error.GetLastError()}");
				Assert.That(b, Is.True);
				TestContext.WriteLine($"Access={(Kernel32.FileAccess)access}; Status={status}");
			}
		}

		[Test()]
		public void AdjustTokenPrivilegesTest()
		{
			using (var t = SafeHTOKEN.FromThread(SafeHTHREAD.Current, TokenAccess.TOKEN_ADJUST_PRIVILEGES | TokenAccess.TOKEN_QUERY))
			{
				Assert.That(LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid));
				var ptp = new PTOKEN_PRIVILEGES(luid, PrivilegeAttributes.SE_PRIVILEGE_ENABLED);
				var old = PTOKEN_PRIVILEGES.GetAllocatedAndEmptyInstance();
				var rLen = (uint)old.Size;
				Assert.That(AdjustTokenPrivileges(t, false, ptp, ptp.SizeInBytes, old, ref rLen));

				rLen = 0U;
				Assert.That(AdjustTokenPrivileges(t, false, ptp, ptp.SizeInBytes, SafeCoTaskMemHandle.Null, ref rLen));
			}
		}

		[Test()]
		public void AllocateAndInitializeSidTest()
		{
			var b = AllocateAndInitializeSid(KnownSIDAuthority.SECURITY_WORLD_SID_AUTHORITY, 1, KnownSIDRelativeID.SECURITY_WORLD_RID, 0, 0, 0, 0, 0, 0, 0, out var pSid);
			Assert.That(b);
			var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
			var esid = new byte[everyone.BinaryLength];
			everyone.GetBinaryForm(esid, 0);
			var peSid = new SafeByteArray(esid);
			Assert.That(EqualSid(pSid, (IntPtr)peSid));
			ConvertStringSidToSid("S-1-2-0", out var lsid);
			Assert.That(EqualSid(pSid, (IntPtr)lsid), Is.False);
			string s = null;
			Assert.That(IsValidSid(pSid), Is.True);
			Assert.That(() => s = ConvertSidToStringSid(pSid), Throws.Nothing);
			Assert.That(s, Is.EqualTo("S-1-1-0"));
			var saptr = GetSidSubAuthority(pSid, 0);
			Assert.That(Marshal.ReadInt32(saptr), Is.EqualTo(0));
			var len = GetLengthSid(pSid);
			var p2 = new SafePSID(len);
			b = CopySid(len, (IntPtr)p2, pSid);
			Assert.That(EqualSid(p2, pSid));
			Assert.That(b);
		}

		[Test()]
		public void AllocateLocallyUniqueIdTest()
		{
			Assert.That(AllocateLocallyUniqueId(out var luid));
			TestContext.WriteLine($"{luid.LowPart:X} {luid.HighPart:X}");
			Assert.That(luid.LowPart, Is.Not.Zero);
		}

		[Test()]
		[PrincipalPermission(SecurityAction.Demand, Role = "Administrators")]
		public void ChangeAndQueryServiceConfigTest()
		{
			using (var sc = new System.ServiceProcess.ServiceController("Netlogon"))
			{
				using (var h = sc.ServiceHandle)
				{
					var hSvc = h.DangerousGetHandle();

					var st = GetStartType();
					var b = ChangeServiceConfig(hSvc, ServiceTypes.SERVICE_NO_CHANGE, ServiceStartType.SERVICE_DISABLED, ServiceErrorControlType.SERVICE_NO_CHANGE);
					if (!b) TestContext.WriteLine($"Err: {Win32Error.GetLastError()}");
					Assert.That(b, Is.True);
					Thread.Sleep(10000);

					Assert.That(GetStartType(), Is.EqualTo(ServiceStartType.SERVICE_DISABLED));
					b = ChangeServiceConfig(hSvc, ServiceTypes.SERVICE_NO_CHANGE, st, ServiceErrorControlType.SERVICE_NO_CHANGE);
					if (!b) TestContext.WriteLine($"Err: {Win32Error.GetLastError()}");
					Assert.That(b, Is.True);
					Assert.That(GetStartType(), Is.EqualTo(st));

					ServiceStartType GetStartType()
					{
						using (var info = new SafeHGlobalHandle(1024))
						{
							Assert.That(QueryServiceConfig(hSvc, (IntPtr)info, (uint)info.Size, out var _), Is.True);
							var qsc = info.ToStructure<QUERY_SERVICE_CONFIG>();
							return qsc.dwStartType;
						}
					}
				}
			}
		}

		[Test()]
		public void ConvertSecurityDescriptorToStringSecurityDescriptorTest()
		{
			var pSD = GetSD(fn);
			var b = ConvertSecurityDescriptorToStringSecurityDescriptor(pSD, SDDL_REVISION.SDDL_REVISION_1,
				SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, out var s, out var len);
			Assert.That(b, Is.True);
			Assert.That(s, Is.Not.Null);
			TestContext.WriteLine(s);
		}

		[Test()]
		public void DuplicateTokenExTest()
		{
			using (var tok = SafeHTOKEN.FromThread(SafeHTHREAD.Current))
			{
				Assert.That(tok.IsInvalid, Is.False);
			}

			using (var tok = SafeHTOKEN.FromThread(GetCurrentThread()))
			{
				Assert.That(tok.IsInvalid, Is.False);
			}
		}

		[Test()]
		public void DuplicateTokenTest()
		{
			using (var pval = SafeHTOKEN.FromProcess(System.Diagnostics.Process.GetCurrentProcess()))
			{
				Assert.That(pval.IsInvalid, Is.False);
				Assert.That(DuplicateToken(pval, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, out var dtok));
				Assert.That(dtok.IsInvalid, Is.False);
				dtok.Close();
			}
		}

		[Test, TestCaseSource(typeof(AdvApi32Tests), nameof(AuthCasesFromFile))]
		public void GetAceTest(bool validUser, bool validCred, string urn, string dn, string dc, string domain, string username, string password, string notes)
		{
			var fun = $"{domain}\\{username}";

			var pSD = GetSD(fn);
			var b = GetSecurityDescriptorDacl(pSD, out var daclPresent, out var pAcl, out var defaulted);
			Assert.That(b, Is.True);
			Assert.That(daclPresent, Is.True);
			Assert.That(pAcl, Is.Not.EqualTo(IntPtr.Zero));
			var hardAcl = ((IntPtr)pAcl).ToStructure<ACL>();
			var ari = new ACL_REVISION_INFORMATION();
			b = GetAclInformation(pAcl, ref ari, (uint)Marshal.SizeOf(typeof(ACL_REVISION_INFORMATION)), ACL_INFORMATION_CLASS.AclRevisionInformation);
			Assert.That(b, Is.True);
			Assert.That(ari.AclRevision, Is.EqualTo(hardAcl.AclRevision));
			var asi = new ACL_SIZE_INFORMATION();
			b = GetAclInformation(pAcl, ref asi, (uint)Marshal.SizeOf(typeof(ACL_SIZE_INFORMATION)), ACL_INFORMATION_CLASS.AclSizeInformation);
			Assert.That(b, Is.True);
			Assert.That(asi.AceCount, Is.EqualTo(hardAcl.AceCount));
			for (var i = 0U; i < asi.AceCount; i++)
			{
				b = GetAce(pAcl, i, out var pAce);
				Assert.That(b, Is.True);

				var accountSize = 1024;
				var domainSize = 1024;
				var outuser = new StringBuilder(accountSize, accountSize);
				var outdomain = new StringBuilder(domainSize, domainSize);
				b = LookupAccountSid(null, pAce.GetSid(), outuser, ref accountSize, outdomain, ref domainSize, out _);
				Assert.That(b, Is.True);
				TestContext.WriteLine($"Ace{i}: {pAce.GetHeader().AceType}={outdomain}\\{outuser}; {pAce.GetMask()}");
			}

			BuildTrusteeWithName(out var pTrustee, fun);
			Assert.That(GetEffectiveRightsFromAcl(pAcl, pTrustee, out var accessRights), Is.EqualTo(Win32Error.ERROR_NONE_MAPPED).Or.Zero);
			var map = new GENERIC_MAPPING((uint)Kernel32.FileAccess.FILE_GENERIC_READ, (uint)Kernel32.FileAccess.FILE_GENERIC_WRITE, (uint)Kernel32.FileAccess.FILE_GENERIC_EXECUTE, (uint)Kernel32.FileAccess.FILE_ALL_ACCESS);
			var ifArray = new SafeInheritedFromArray(hardAcl.AceCount);
			var err = GetInheritanceSource(fn, SE_OBJECT_TYPE.SE_FILE_OBJECT, SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, false, null,
				0, pAcl, IntPtr.Zero, map, ifArray);
			Assert.That(err, Is.EqualTo(0));
			TestContext.WriteLine($"{hardAcl.AceCount}: {string.Join("; ", ifArray.Results.Select(i => i.ToString()))}");
			Assert.That(() => ifArray.Dispose(), Throws.Nothing);
		}

		[Test()]
		public void GetNamedSecurityInfoTest()
		{
			using (var pSD = GetSD(fn))
				Assert.That(pSD, Is.Not.Null);
		}

		[Test()]
		public void GetPrivateObjectSecurityTest()
		{
			using (var pSD = GetSD(fn))
			{
				var pos = pSD.GetPrivateObjectSecurity(SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION);
				Assert.That(!pos.IsInvalid);
			}
		}

		[Test()]
		public void GetTokenInformationTest()
		{
			//var p = SafeTokenHandle.CurrentProcessToken.GetInfo<PTOKEN_PRIVILEGES>(TOKEN_INFORMATION_CLASS.TokenPrivileges).Privileges;
			using (var t = SafeHTOKEN.FromProcess(Process.GetCurrentProcess(), TokenAccess.TOKEN_QUERY))
			{
				Assert.That(t, Is.Not.Null);

				var p = t.GetInfo<PTOKEN_PRIVILEGES>(TOKEN_INFORMATION_CLASS.TokenPrivileges);
				Assert.That(p, Is.Not.Null);
				Assert.That(p.PrivilegeCount, Is.GreaterThan(0));
				TestContext.WriteLine("Privs: " + string.Join("; ", p.Privileges.Select(i => i.ToString())));

				using (var hMem = PTOKEN_PRIVILEGES.GetAllocatedAndEmptyInstance(50))
				{
					var b = GetTokenInformation(t, TOKEN_INFORMATION_CLASS.TokenPrivileges, hMem, hMem.Size, out var sz);
					Assert.That(b);
					var p1 = PTOKEN_PRIVILEGES.FromPtr((IntPtr)hMem);
					Assert.That(p1.PrivilegeCount, Is.EqualTo(p.PrivilegeCount));

					Assert.That(p.Privileges, Is.EquivalentTo(p1.Privileges));
				}
			}

			using (var t = SafeHTOKEN.FromThread(GetCurrentThread(), TokenAccess.TOKEN_QUERY))
			{
				var id = t.GetInfo<uint>(TOKEN_INFORMATION_CLASS.TokenSessionId);
				Assert.That(id, Is.Not.Zero);
				TestContext.WriteLine($"SessId: {id}");

				var ve = t.GetInfo<uint>(TOKEN_INFORMATION_CLASS.TokenVirtualizationEnabled);
				Assert.That(ve, Is.Zero);
				TestContext.WriteLine($"VirtEnable: {ve}");

				var et = t.GetInfo<TOKEN_ELEVATION_TYPE>(TOKEN_INFORMATION_CLASS.TokenElevationType);
				Assert.That(et, Is.Not.Zero);
				TestContext.WriteLine($"ElevType: {et}");

				var e = t.GetInfo<TOKEN_ELEVATION>(TOKEN_INFORMATION_CLASS.TokenElevation);
				Assert.That(e, Is.Not.Zero);
				TestContext.WriteLine($"Elev: {e.TokenIsElevated}");
			}
		}

		[Test()]
		public void InitAndAbortSystemShutdownTest()
		{
			//Assert.That(InitiateShutdown(null, "InitiateShutdown test", 60, ShutdownFlags.SHUTDOWN_RESTART | ShutdownFlags.SHUTDOWN_HYBRID,
			//	SystemShutDownReason.SHTDN_REASON_MAJOR_APPLICATION | SystemShutDownReason.SHTDN_REASON_MINOR_MAINTENANCE |
			//	SystemShutDownReason.SHTDN_REASON_FLAG_PLANNED), Is.EqualTo(Win32Error.ERROR_ACCESS_DENIED));
			//using (new PrivilegedCodeBlock(SystemPrivilege.Shutdown))
			{
				var e = InitiateShutdown(null, "InitiateShutdown test", 60, ShutdownFlags.SHUTDOWN_RESTART | ShutdownFlags.SHUTDOWN_HYBRID,
					SystemShutDownReason.SHTDN_REASON_MAJOR_APPLICATION | SystemShutDownReason.SHTDN_REASON_MINOR_MAINTENANCE |
					SystemShutDownReason.SHTDN_REASON_FLAG_PLANNED);
				Assert.That(e, Is.EqualTo(0));
				Thread.Sleep(2000);
				var b = AbortSystemShutdown(null);
				Assert.That(b);
			}
		}

		[Test()]
		public void InitiateSystemShutdownExTest()
		{
			//Assert.That(InitiateSystemShutdownEx(null, "InitiateSystemShutdownEx test", 60, false, true,
			//	SystemShutDownReason.SHTDN_REASON_MAJOR_APPLICATION | SystemShutDownReason.SHTDN_REASON_MINOR_MAINTENANCE |
			//	SystemShutDownReason.SHTDN_REASON_FLAG_PLANNED), Is.False);
			//using (new PrivilegedCodeBlock(SystemPrivilege.Shutdown))
			{
				var e = InitiateSystemShutdownEx(null, "InitiateSystemShutdownEx test", 60, false, true,
					SystemShutDownReason.SHTDN_REASON_MAJOR_APPLICATION | SystemShutDownReason.SHTDN_REASON_MINOR_MAINTENANCE |
					SystemShutDownReason.SHTDN_REASON_FLAG_PLANNED);
				Assert.That(e, Is.True);
				Thread.Sleep(2000);
				var b = AbortSystemShutdown(null);
				Assert.That(b);
			}
		}

		[Test, TestCaseSource(typeof(AdvApi32Tests), nameof(AuthCasesFromFile))]
		public void LogonUserExTest(bool validUser, bool validCred, string urn, string dn, string dcn, string domain, string un, string pwd, string notes)
		{
			var b = LogonUserEx(urn, null, pwd, LogonUserType.LOGON32_LOGON_INTERACTIVE, LogonUserProvider.LOGON32_PROVIDER_DEFAULT, out var hTok, out _, out _, out _, out _);
			if (!b) TestContext.WriteLine(Win32Error.GetLastError());
			Assert.That(b, Is.EqualTo(validCred && validUser));
			hTok.Dispose();
		}

		[Test, TestCaseSource(typeof(AdvApi32Tests), nameof(AuthCasesFromFile))]
		public void LogonUserTest(bool validUser, bool validCred, string urn, string dn, string dcn, string domain, string un, string pwd, string notes)
		{
			var b = LogonUser(urn, null, pwd, LogonUserType.LOGON32_LOGON_INTERACTIVE, LogonUserProvider.LOGON32_PROVIDER_DEFAULT, out var hTok);
			if (!b) TestContext.WriteLine(Win32Error.GetLastError());
			Assert.That(b, Is.EqualTo(validCred && validUser));
			hTok.Dispose();
		}

		[Test, TestCaseSource(typeof(AdvApi32Tests), nameof(AuthCasesFromFile))]
		public void LookupAccountNameTest(bool validUser, bool validCred, string urn, string dn, string dc, string domain, string username, string password, string notes)
		{
			var fun = $"{domain}\\{username}";
			TestContext.WriteLine(fun);
			Assert.That(LookupAccountName(null, fun, out var sid, out var dom, out var snu), Is.EqualTo(validUser));
			Assert.That(sid.IsValidSid, Is.EqualTo(validUser));
			if (!validUser) return;

			Assert.That(dom, Is.EqualTo(domain).IgnoreCase);
			Assert.That(snu, Is.EqualTo(SID_NAME_USE.SidTypeUser));

			int chName = 1024, chDom = 1024;
			var name = new StringBuilder(chName);
			var domN = new StringBuilder(chDom);
			Assert.That(LookupAccountSid(null, sid, name, ref chName, domN, ref chDom, out snu));
			Assert.That(name.ToString(), Is.EqualTo(username).IgnoreCase);
			Assert.That(domN.ToString(), Is.EqualTo(domain).IgnoreCase);
			Assert.That(snu, Is.EqualTo(SID_NAME_USE.SidTypeUser));
		}

		[Test()]
		public void LookupPrivilegeNameValueTest()
		{
			const string priv = "SeBackupPrivilege";
			Assert.That(LookupPrivilegeValue(null, priv, out var luid));
			var chSz = 100;
			var sb = new StringBuilder(chSz);
			Assert.That(LookupPrivilegeName(null, luid, sb, ref chSz));
			Assert.That(sb.ToString(), Is.EqualTo(priv));

			// Look at bad values
			Assert.That(LookupPrivilegeValue(null, "SeBadPrivilege", out luid), Is.False);
			luid = LUID.NewLUID();
			Assert.That(LookupPrivilegeName(null, luid, sb, ref chSz), Is.False);
		}

		[Test()]
		public void OpenCloseSCManager()
		{
			using (var scm = AdvApi32.OpenSCManager(null, null, ScManagerAccessTypes.SC_MANAGER_CONNECT))
			{
				AssertHandleIsValid(scm);
			}
		}

		[Test()]
		public void OpenCloseService()
		{
			using (var scm = AdvApi32.OpenSCManager(null, null, ScManagerAccessTypes.SC_MANAGER_CONNECT))
			{
				AssertHandleIsValid(scm);

				//opens task scheduler service
				using (var service = AdvApi32.OpenService(scm, "Schedule", ServiceAccessTypes.SERVICE_QUERY_STATUS))
				{
					AssertHandleIsValid(service);
				}
			}
		}

		[Test()]
		public void PrivilegeCheckTest()
		{
			using (var t = SafeHTOKEN.FromProcess(GetCurrentProcess(), TokenAccess.TOKEN_QUERY))
			{
				Assert.That(LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid));
				Assert.That(PrivilegeCheck(t, new PRIVILEGE_SET(PrivilegeSetControl.PRIVILEGE_SET_ALL_NECESSARY, luid, PrivilegeAttributes.SE_PRIVILEGE_ENABLED), out var res));
				TestContext.WriteLine($"Has {luid}={res}");

				Assert.That(LookupPrivilegeValue(null, "SeChangeNotifyPrivilege", out luid));
				Assert.That(PrivilegeCheck(t, new PRIVILEGE_SET(PrivilegeSetControl.PRIVILEGE_SET_ALL_NECESSARY, new[] { new LUID_AND_ATTRIBUTES(luid, PrivilegeAttributes.SE_PRIVILEGE_ENABLED_BY_DEFAULT), new LUID_AND_ATTRIBUTES(luid, PrivilegeAttributes.SE_PRIVILEGE_ENABLED) }), out res));
				TestContext.WriteLine($"Has {luid}={res}");

				Assert.That(LookupPrivilegeValue(null, "SeShutdownPrivilege", out luid));
				Assert.That(PrivilegeCheck(t, new PRIVILEGE_SET(PrivilegeSetControl.PRIVILEGE_SET_ALL_NECESSARY, luid, PrivilegeAttributes.SE_PRIVILEGE_ENABLED), out res));
				TestContext.WriteLine($"Has {luid}={res}");
			}
		}

		[Test()]
		[PrincipalPermission(SecurityAction.Demand, Role = "Administrators")]
		public void QueryServiceConfig2Test()
		{
			using (var sc = new System.ServiceProcess.ServiceController("Netlogon"))
			{
				using (var h = sc.ServiceHandle)
				{
					var hSvc = h.DangerousGetHandle();

					var b = QueryServiceConfig2(hSvc, ServiceConfigOption.SERVICE_CONFIG_DESCRIPTION, out SERVICE_DESCRIPTION sd);
					Assert.That(b, Is.True);
					Assert.That(sd.lpDescription, Is.Not.Null);
					TestContext.WriteLine(sd.lpDescription);
				}
			}
		}

		[Test()]
		public void QueryServiceStatus()
		{
			using (var scm = AdvApi32.OpenSCManager(null, null, ScManagerAccessTypes.SC_MANAGER_CONNECT))
			{
				AssertHandleIsValid(scm);

				//opens task scheduler service
				using (var service = AdvApi32.OpenService(scm, "Schedule", ServiceAccessTypes.SERVICE_QUERY_STATUS))
				{
					AssertHandleIsValid(service);

					//query service status
					var status = AdvApi32.QueryServiceStatusEx<SERVICE_STATUS_PROCESS>(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO);

					Assert.That(status.dwServiceType, Is.EqualTo(ServiceTypes.SERVICE_WIN32).Or.EqualTo(ServiceTypes.SERVICE_WIN32_SHARE_PROCESS));
					Assert.That(status.dwServiceFlags, Is.EqualTo(0));
				}
			}
		}

		[Test()]
		public void RegNotifyChangeKeyValueTest()
		{
			const string tmpRegKey = "Software\\____TmpRegKey____";
			new Thread(() =>
			{
				Thread.Sleep(1000);
				Microsoft.Win32.Registry.CurrentUser.CreateSubKey(tmpRegKey);
				Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(tmpRegKey);
			}).Start();
			Assert.That(RegOpenKeyEx(HKEY.HKEY_CURRENT_USER, "Software", RegOpenOptions.REG_OPTION_NON_VOLATILE, RegAccessTypes.KEY_NOTIFY,
				out var h), Is.EqualTo(0));
			var hEvent = CreateEvent(null, true, false);
			Assert.That(RegNotifyChangeKeyValue(h, false, RegNotifyChangeFilter.REG_NOTIFY_CHANGE_NAME, hEvent, true), Is.EqualTo(0));
			var b = WaitForSingleObject(hEvent, 5000);
			Assert.That(b == WAIT_STATUS.WAIT_OBJECT_0);
		}

		[Test()]
		[PrincipalPermission(SecurityAction.Demand, Role = "Administrators")]
		public void SetNamedSecurityInfoTest()
		{
			using (var pSD = GetSD(fn))
			{
				Assert.That(GetSecurityDescriptorOwner(pSD, out var owner, out var def));
				Assert.That((IntPtr)owner, Is.Not.EqualTo(IntPtr.Zero));

				var admins = new SafePSID("S-1-5-32-544");

				var err = SetNamedSecurityInfo(fn, SE_OBJECT_TYPE.SE_FILE_OBJECT, SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION, admins, PSID.NULL, IntPtr.Zero, IntPtr.Zero);
				if (err.Failed) TestContext.WriteLine($"SetNamedSecurityInfo failed: {err}");
				Assert.That(err.Succeeded);
				err = SetNamedSecurityInfo(fn, SE_OBJECT_TYPE.SE_FILE_OBJECT, SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION, owner, PSID.NULL, IntPtr.Zero, IntPtr.Zero);
				if (err.Failed) TestContext.WriteLine($"SetNamedSecurityInfo failed: {err}");
				Assert.That(err.Succeeded);
			}
		}

		[Test()]
		[PrincipalPermission(SecurityAction.Demand, Role = "Administrators")]
		public void StartStopService()
		{
			using (var scm = AdvApi32.OpenSCManager(null, null, ScManagerAccessTypes.SC_MANAGER_CONNECT))
			{
				AssertHandleIsValid(scm);

				var access = ServiceAccessTypes.SERVICE_START | ServiceAccessTypes.SERVICE_STOP | ServiceAccessTypes.SERVICE_QUERY_STATUS;

				//opens print spooler service
				using (var service = AdvApi32.OpenService(scm, "Spooler", access))
				{
					AssertHandleIsValid(service);

					//query service status
					var status = AdvApi32.QueryServiceStatusEx<SERVICE_STATUS_PROCESS>(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO);

					if (status.dwCurrentState == ServiceState.SERVICE_RUNNING)
					{
						var ret4 = AdvApi32.StopService(service, out var _);
						if (!ret4) Win32Error.ThrowLastError();

						WaitForServiceStatus(service, ServiceState.SERVICE_STOPPED);

						var ret6 = AdvApi32.StartService(service);
						if (!ret6) Win32Error.ThrowLastError();
					}
					else
					{
						var ret4 = AdvApi32.StartService(service);
						if (!ret4) Win32Error.ThrowLastError();

						WaitForServiceStatus(service, ServiceState.SERVICE_RUNNING);

						var ret6 = AdvApi32.StopService(service, out var _);
						if (!ret6) Win32Error.ThrowLastError();
					}
				}
			}
		}

		[Test]
		public void UserTest()
		{
			GetNamedSecurityInfo(fn, SE_OBJECT_TYPE.SE_FILE_OBJECT, SECURITY_INFORMATION.DACL_SECURITY_INFORMATION, out _, out _, out var ppDacl, out _, out var ppSecurityDescriptor).ThrowIfFailed();

			var aceCount = ppDacl.GetAclInformation<ACL_SIZE_INFORMATION>().AceCount;
			for (var i = 0U; i < aceCount; i++)
			{
				if (!GetAce(ppDacl, i, out var ace)) Win32Error.ThrowLastError();
				var accountSize = 1024;
				var domainSize = 1024;
				var account = new StringBuilder(accountSize, accountSize);
				var domain = new StringBuilder(domainSize, domainSize);
				if (!LookupAccountSid(null, ace.GetSid(), account, ref accountSize, domain, ref domainSize, out _)) Win32Error.ThrowLastError();
				TestContext.WriteLine($"Ace{i}: {ace.GetHeader().AceType}={domain}\\{account}; {ace.GetMask()}");
			}
		}
		internal static SafeSecurityDescriptor GetSD(string filename, SECURITY_INFORMATION si = SECURITY_INFORMATION.DACL_SECURITY_INFORMATION | SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION)
		{
			var err = GetNamedSecurityInfo(filename, SE_OBJECT_TYPE.SE_FILE_OBJECT, si, out _, out _, out _, out _, out var pSD);
			Assert.That(err, Is.EqualTo(0));
			Assert.That(!pSD.IsInvalid);
			return pSD;
		}

		private static void AssertHandleIsValid(SafeSC_HANDLE handle)
		{
			if (handle.IsInvalid)
				Win32Error.ThrowLastError();

			Assert.That(handle.IsNull, Is.False);
			Assert.That(handle.IsClosed, Is.False);
			Assert.That(handle.IsInvalid, Is.False);
		}

		private static void WaitForServiceStatus(SafeSC_HANDLE service, ServiceState status)
		{
			//query service status again to check that it changed
			var tests = 0;

			while (tests < 40)
			{
				var status2 = AdvApi32.QueryServiceStatusEx<SERVICE_STATUS_PROCESS>(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO);
				if (status2.dwCurrentState == status)
					break;

				Thread.Sleep(500);
				tests++;
			}

			if (tests >= 40)
				throw new TimeoutException($"Timed-out waiting for service status {status}");
		}
	}
}