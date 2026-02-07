# RoboCaty

**RoboCaty** is a lightweight utility designed to facilitate cyclic, direct variable exchange between a **Beckhoff TwinCAT PLC** and an **ABB Virtual Robot Controller** (RobotStudio) via ADS.

It acts as a bridge during virtual commissioning, allowing for signal synchronization between the PLC and the Robot without the need for complex fieldbus hardware emulation.

![RoboCaty Demo](robocaty.gif)
---

## 1. Prerequisites & Compatibility

This program was developed specifically for virtual commissioning projects. While it may work with other configurations, it has been tested and verified with the following dependencies:

*   **TwinCAT 3:** Version 3.1.4024 or 3.1.4026 (recent builds)
*   **Framework:** .NET 8.0
*   **ABB Robotics PC SDK:** Version 2025.4
*   **ABB RobotStudio:** Version 2025.4
*   **Controller:** Omnicore C90XT virtual controller with RobotWare 7.20.0

---

## 2. Configuration (`vars.txt`)

The variables to be exchanged are defined in a simple text file. By default, the program looks for `C:\temp\vars_robot.txt`, but this can be changed via arguments.

### Syntax
Each line in the file represents one mapping:

```text
[Direction]#[TwinCAT_Path]:[Robot_Signal][Size]
```

Example: r#MAIN.boStop:diStop[1]

### Parameter Definition

| Parameter | Description |
| :--- | :--- |
| **Direction** | `r` = **Read** from TwinCAT ADS $\rightarrow$ **Write** to Robot Virtual Controller.<br>`w` = **Read** from Robot Virtual Controller $\rightarrow$ **Write** to TwinCAT ADS. |
| **#** | Separator. |
| **TwinCAT_Path** | The full path to the ADS variable (e.g., `MAIN.boStop` or `GVL.bStart`). |
| **:** | Separator. |
| **Robot_Signal** | The exact name of the I/O signal on the ABB Virtual Controller. |
| **[Size]** | The size of the signal in bits.<br>`[1]` = Boolean (Digital I/O)<br>`[8]`, `[16]`, `[32]` = Integer (Group I/O or Analog) |

### Example Content
See the `vars_robot.txt` file in the root of this repository for a full template.

```text
r#MAIN.bStartRobot:di_StartSignal[1]
w#MAIN.bRobotActive:do_RobotActive[1]
r#GVL.nSpeedSetpoint:grp_SpeedInput[16]
w#GVL.nCurrentPos:grp_PositionOutput[32]
```

---

## 3. Usage

Run `RoboCaty.exe` from the command line. You can customize the behavior using the arguments below.

### Command Line Arguments

| Argument | Description | Default Value |
| :--- | :--- | :--- |
| `-help` | Shows the help output and available commands. | N/A |
| `-verbose` | Enables a live dashboard showing the values of all exchanged variables in the console. | `false` |
| `-netid` | The AMS NetID of the TwinCAT runtime to connect to. | `199.4.42.250.1.1` (UMRT default) |
| `-file` | File path to the configuration file. | `C:\temp\vars_robot.txt` |
| `-time` | Cycle time (in milliseconds) for the data exchange loop. | `2000` |

### Example Call
```powershell
RoboCaty.exe -netid 5.12.80.1.1.1 -file "config.txt" -time 100 -verbose
```

---

## Contributing

If you encounter any issues or want to contribute to this project, feel free to open an **Issue** or submit a **Pull Request** on this repository.