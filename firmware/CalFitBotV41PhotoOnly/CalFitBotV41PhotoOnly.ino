#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <esp_system.h>
WebServer server(80);
bool sleeping=false, pendingScan=false, acked=false, captureRequested=false;
uint32_t lastTouch=0, sleepFrame=0, captureAt=0;
uint8_t *scanPhoto=nullptr;
uint32_t scanBytes=0;
float scanWeight=0;
String scanId;
void networkBegin();
void networkTick();
void standbyTick();
void requestCapture();
bool weightValid();
void wifiIcon();
// Test 20000000 after confirming continuous capture is stable.
constexpr uint32_t TFT_SPI_HZ = 10000000;
#include <TJpg_Decoder.h>
#include <esp_heap_caps.h>
#include <HX711.h>
#include <Preferences.h>
#include <math.h>
#include <SPI.h>
#include <Adafruit_GFX.h>
#include <Adafruit_ST7789.h>
#include <XPT2046_Touchscreen.h>
#define TFT_CS 10
#define TFT_DC 9
#define TFT_RST 8
#define SPI_MOSI 11
#define SPI_SCLK 12
#define SPI_MISO 13
#define TOUCH_CS 7
#define TOUCH_MIN_X 200
#define TOUCH_MAX_X 3800
#define TOUCH_MIN_Y 200
#define TOUCH_MAX_Y 3800
#define SWAP_XY false
#define INVERT_X false
#define INVERT_Y false
Adafruit_ST7789 tft(&SPI,TFT_CS,TFT_DC,TFT_RST);
XPT2046_Touchscreen touch(TOUCH_CS);
constexpr int MOTOR_ENA=6, MOTOR_IN1=18, MOTOR_IN2=21;
// Hold-to-run: no fixed duration; release or leaving button stops motor.
constexpr int CTRL_X=6, CTRL_W=100, CTRL_H=39, CTRL_Y=173;
int cameraAction(int x,int y){
 if(y<CTRL_Y || y>=CTRL_Y+CTRL_H)return 0;
 if(x>=6 && x<106)return 1;
 if(x>=110 && x<210)return 2;
 if(x>=214 && x<314)return -1;
 return 0;
}
int motorDirection=0;

bool photoFrozen=false;
uint32_t photoAt=0;
void motorStop(){digitalWrite(MOTOR_ENA,LOW);digitalWrite(MOTOR_IN1,LOW);digitalWrite(MOTOR_IN2,LOW);if(motorDirection) Serial.println("[MOTOR] STOP");motorDirection=0;}
void motorStart(int dir){motorStop();digitalWrite(MOTOR_IN1,dir>0?HIGH:LOW);digitalWrite(MOTOR_IN2,dir<0?HIGH:LOW);motorDirection=dir;digitalWrite(MOTOR_ENA,HIGH);Serial.printf("[MOTOR] %s | ENA=%d IN1=%d IN2=%d\n",dir>0?"UP":"DOWN",digitalRead(MOTOR_ENA),digitalRead(MOTOR_IN1),digitalRead(MOTOR_IN2));}
void motorButtons();
void cameraBegin();
void cameraTick();
void cameraUI(bool full);
void resetFilter();
void updateFilter(long raw);
void updateUI();
void handleTouch();
HX711 scale;
Preferences prefs;
bool prefsOK=false, factorSaved=false;
long testDelta=0, testSpan=0;
constexpr int N=200;
float factor=0, reference=0;
constexpr int MAX_POINTS=8;
double masses[MAX_POINTS], deltas[MAX_POINTS]; int points=0;
bool adding=false;
long offset=0, previous=0, tareRange=0, lo, hi;
int count=0, operation=0;
int64_t sum=0;
bool tared=false, havePrevious=false;
String command;
unsigned long last=0, started=0, lastWarning=0;
String notice="Tare empty platform";
void beginCollect(int op){notice=op==1?"Keep platform empty":"Keep reference still";operation=op;count=0;sum=0;lo=8388607;hi=-8388608;started=millis();Serial.println(op==1?"Taring: keep platform empty":"Calibrating: keep reference weight still");}
void execute(String cmd){
 cmd.trim();
 if(cmd=="s"){Serial.printf("Storage: %s | Factor saved: %s | Tare range: %ld\n",prefsOK?"ready":"unavailable",factorSaved?"yes":"no",tareRange);Serial.printf("Settings: factor %.6f | offset %ld | tared %s\n",factor,offset,tared?"yes":"no");return;}
 if(cmd=="x"){operation=0;notice="Collection cancelled";Serial.println("Collection cancelled");return;}
 if(operation){notice="Busy: wait or cancel";Serial.println("BUSY: wait or cancel with x");return;}
 if(cmd=="t"||cmd=="z"){beginCollect(1);}
 else if(cmd=="d"){if(!tared){notice="Tare first";Serial.println("ERROR: tare first");return;}beginCollect(3);notice="Testing loaded platform";}
 else if((cmd.startsWith("c ")||cmd.startsWith("a "))){
  String arg=cmd.substring(2);char *end;const char *p=arg.c_str();float g=strtof(p,&end);
  if(end==p||*end||!isfinite(g)||g<=0||g>5000||!tared){notice="Tare first / invalid reference";Serial.println("ERROR: tare first and enter positive grams");return;}
  adding=cmd.startsWith("a ");
  if(adding&&(points==0||points>=MAX_POINTS)){notice="Calibrate first / point limit";Serial.println("ERROR: calibrate first or point limit reached");return;}
  reference=g;beginCollect(2);
 }else if(cmd=="r"){factor=0;tared=false;points=0;resetFilter();notice="Calibration cleared";factorSaved=false;if(prefsOK && prefs.isKey("factor") && !prefs.remove("factor")){notice="Flash reset failed";Serial.println("ERROR: saved factor not erased");}Serial.println("Calibration cleared; raw mode");}
 else Serial.println("Commands: t, z, d (response test), c 500, a 1000, r, s, x");
}
void collect(long raw){
 if(!operation)return;
 if(raw<=-8388608||raw>=8388607){operation=0;notice="ADC saturation";Serial.println("ERROR: ADC saturation; collection aborted");return;}
 sum+=raw;lo=min(lo,raw);hi=max(hi,raw);count++;
 if(count%10==0)Serial.printf("Collecting: %d/%d\n",count,N);
 if(count<N)return;
 double mean=double(sum)/N;long span=hi-lo;int op=operation;operation=0;
 if(op==1){points=0;resetFilter();offset=lround(mean);tareRange=span;tared=true;notice="Tare complete";Serial.printf("Tare complete: %ld | Range: %ld\n",offset,span);return;}
 double delta=mean-offset;long noise=max(tareRange,span);
 testDelta=lround(delta);testSpan=span;
 Serial.printf("[MEASURE] Mean: %.1f | Min: %ld | Max: %ld | Range: %ld | Offset: %ld\n",mean,lo,hi,span,offset);
 Serial.printf("[RESPONSE] Delta: %.1f | Tare noise: %ld | Loaded noise: %ld | Required: %.0f | Ratio: %.2f\n",delta,tareRange,span,max(100.0,10.0*noise),fabs(delta)/max(1L,noise));
 if(op==3){notice=String("Test: ")+String(testDelta)+" counts / noise "+String(noise);Serial.println(fabs(delta)>=max(100.0,10.0*noise)?"[TEST] Response sufficient":"[TEST] Weak response: check load transfer and wiring");return;}
 if(fabs(delta)<100||fabs(delta)<10.0*noise){notice=String("Rejected: ")+String(testDelta)+" / need "+String(max(100.0,10.0*noise),0);Serial.printf("ERROR: calibration rejected; response %.0f counts, noise %ld; use larger known weight or stabilize sensor\n",delta,noise);return;}
 int n=adding?points:0;
 double xx=reference*reference, xy=reference*delta;
 for(int i=0;i<n;i++){if(fabs(masses[i]-reference)<0.01||deltas[i]*delta<=0){notice="Duplicate/polarity error";Serial.println("ERROR: duplicate mass or inconsistent polarity");return;} xx+=masses[i]*masses[i];xy+=masses[i]*deltas[i];}
 float f=xy/xx;
 if(n && fabs(delta/f-reference)>max(3.0,0.02*reference)){notice="Point inconsistent";Serial.println("ERROR: new point inconsistent; check mounting and tare");return;}
 if(!isfinite(f)||fabsf(f)<0.01f){notice="Invalid factor";Serial.println("ERROR: invalid factor");return;}
 masses[n]=reference;deltas[n]=delta;points=n+1;factor=f;resetFilter();
 Serial.printf("[CAL] Applied factor: %.6f counts/g\n",factor);
 factorSaved=false;
 if(prefsOK && prefs.putFloat("factor",factor)==sizeof(float)){
   float check=prefs.getFloat("factor",0);
   factorSaved=isfinite(check)&&check==factor;
 }
 notice=factorSaved?"Calibration saved":"Calibration RAM only: save failed";
 Serial.printf("%s: %.6f counts/g | Range: %ld | Points: %d\n",factorSaved?"Calibration saved":"WARNING: calibration active in RAM only",factor,span,points);
}
void setup(){
 digitalWrite(MOTOR_ENA,LOW);pinMode(MOTOR_ENA,OUTPUT);pinMode(MOTOR_IN1,OUTPUT);pinMode(MOTOR_IN2,OUTPUT);motorStop();
 Serial.begin(115200); delay(1500);
 Serial.println("[BOOT] CalFitBot V4.1 WiFi Scan Standby");
 Serial.printf("[INFO] Flash: %u | PSRAM: %u\n", ESP.getFlashChipSize(), ESP.getPsramSize());
 pinMode(TOUCH_CS,OUTPUT); digitalWrite(TOUCH_CS,HIGH);
 pinMode(TFT_CS,OUTPUT); digitalWrite(TFT_CS,HIGH);
 Serial.println("[INIT] SPI");
 SPI.begin(SPI_SCLK,SPI_MISO,SPI_MOSI,TFT_CS);
 Serial.println("[OK] SPI initialized");
 Serial.println("[INIT] TFT");
 tft.init(240,320); tft.setSPISpeed(TFT_SPI_HZ); tft.setRotation(3); tft.invertDisplay(false);
 tft.fillScreen(ST77XX_BLACK); tft.setTextWrap(false);
 Serial.println("[OK] TFT initialization returned");
 Serial.println("[INIT] Touch");
 bool touchOK=touch.begin(SPI); touch.setRotation(1);
 Serial.println(touchOK?"[OK] Touch initialization returned":"[ERROR] Touch initialization failed");
 Serial.println("[INIT] HX711");
 // Photo-only mode: HX711 not initialized.
 Serial.println("[OK] Photo-only capture; weight estimated by CalFit AI");
 Serial.println("[INIT] Preferences");
 if((prefsOK=prefs.begin("hx-standalone",false))){
   factor=prefs.getFloat("factor",0);
   Serial.println("[OK] Preferences opened");
 }else{notice="Preferences open failed";Serial.println("[ERROR] Preferences open failed");}
 if(!isfinite(factor)||fabsf(factor)<0.01f)factor=0;
 factorSaved=prefsOK&&factor!=0;
 Serial.printf("[INFO] Calibration factor: %.6f\n",factor);
 Serial.println("[INFO] Tare required after boot");
 cameraBegin();
 networkBegin();
 lastTouch=millis();
 last=millis();
 Serial.println("[READY] CalFitBot running");
}
void loop(){
 handleTouch();
 // Keep touch polling responsive while motor is energized.
 if(motorDirection){delay(2);return;}
 networkTick();standbyTick();
 if(!sleeping)updateUI();cameraTick();delay(2);
}

// On-device filtering and touchscreen UI. Serial protocol remains V3 compatible.
float med[5], history[100], filtered=0, drift=0, noise=0;
int mn=0, mi=0, hn=0, hiIndex=0;
bool filterReady=false, saturated=false;
unsigned long settleAt=0, uiAt=0;
long displayRaw=0;
int grams=500, page=0;
bool pressed=false, redraw=true;
void resetFilter(){mn=mi=hn=hiIndex=0;filterReady=false;drift=noise=0;settleAt=millis();}
void updateFilter(long raw){
 displayRaw=raw;saturated=raw<=-8388608||raw>=8388607;
 if(saturated){resetFilter();return;}
 if(!tared||factor==0){filterReady=false;return;}
 float w=(raw-offset)/factor;
 med[mi]=w;mi=(mi+1)%5;mn=min(mn+1,5);
 float sorted[5];for(int i=0;i<mn;i++)sorted[i]=med[i];
 for(int i=1;i<mn;i++){float v=sorted[i];int j=i;while(j>0&&sorted[j-1]>v){sorted[j]=sorted[j-1];j--;}sorted[j]=v;}
 float m=sorted[mn/2];
 if(!filterReady){filtered=m;filterReady=true;settleAt=millis();}
 if(fabsf(m-filtered)>3){hn=hiIndex=0;settleAt=millis();filtered+=0.65f*(m-filtered);}
 else filtered+=0.15f*(m-filtered);
 history[hiIndex]=w;hiIndex=(hiIndex+1)%100;hn=min(hn+1,100);
 int n=min(hn,20);float low=w,high=w;for(int i=0;i<n;i++){float v=history[(hiIndex+99-i)%100];low=min(low,v);high=max(high,v);}noise=high-low;
 drift=0;if(hn==100){float a=0,b=0;for(int i=0;i<20;i++){a+=history[(hiIndex+i)%100];b+=history[(hiIndex+80+i)%100];}drift=fabsf(b-a)/20;}
}
uint16_t panel=0x2124, cyan=0x05D7;
constexpr uint16_t DASH_BG=0x1082, DASH_MUTED=0xAD55;
GFXcanvas16 weightCanvas(155,32);
void labelAt(int x,int y,const String &s,uint16_t color=ST77XX_WHITE,int size=1){tft.setCursor(x,y);tft.setTextColor(color);tft.setTextSize(size);tft.print(s);}
void btn(int x,int y,int w,int h,const char *s){tft.fillRoundRect(x,y,w,h,5,panel);labelAt(x+(w-6*strlen(s))/2,y+(h-8)/2,s);}
// Opaque text overwrites only changed fields; no periodic screen clearing.
GFXcanvas16 fieldCanvas(312,32);
String cached[6]; uint16_t cachedColor[6]={};
void field(int i,int x,int y,int size,int chars,String value,uint16_t color=ST77XX_WHITE){
 if(value.length()>chars)value=value.substring(0,chars);
 while(value.length()<chars)value+=' ';
 if(cached[i]==value && cachedColor[i]==color)return;
 cached[i]=value;cachedColor[i]=color;
 if(fieldCanvas.getBuffer()){
   fieldCanvas.fillScreen(ST77XX_BLACK);fieldCanvas.setTextWrap(false);
   fieldCanvas.setTextSize(size);fieldCanvas.setTextColor(color);fieldCanvas.setCursor(0,0);fieldCanvas.print(value);
   tft.drawRGBBitmap(x,y,fieldCanvas.getBuffer(),312,8*size);
 }else{tft.setTextSize(size);tft.setTextColor(color,ST77XX_BLACK);tft.setCursor(x,y);tft.print(value);}
}
void updateUI(){
 if(!redraw&&millis()-uiAt<200)return;uiAt=millis();
 static int oldOperation=-1;
 bool full=redraw;
 bool operationChanged=oldOperation!=operation;oldOperation=operation;
 if(page==4){redraw=false;cameraUI(full);return;}
 if(full){redraw=false;tft.fillScreen(ST77XX_BLACK);for(int i=0;i<6;i++)cached[i]="";
 tft.fillRect(0,0,320,32,panel);labelAt(8,8,"CalFitBot | Photo",ST77XX_WHITE,2);
 if(page==0){
 tft.fillScreen(DASH_BG);
 labelAt(12,10,"CalFitBot",cyan,2);
 tft.fillRoundRect(12,55,296,82,9,panel);
 labelAt(112,63,"PHOTO MODE",DASH_MUTED);
 btn(4,143,312,36,"OPEN CAMERA");
 }
 else if(page==1){labelAt(8,38,"Reference weight",cyan,2);btn(8,123,70,30,"-100");btn(86,123,70,30,"-10");btn(164,123,70,30,"+10");btn(242,123,70,30,"+100");btn(4,185,100,48,"BACK");btn(110,185,100,48,"CALIBRATE");btn(216,185,100,48,"ADD POINT");}
 else if(page==3){labelAt(8,42,"Platform empty?",cyan,2);labelAt(8,75,"Remove all food before taring.");btn(4,185,100,48,"CANCEL");btn(110,185,100,48,"CONFIRM");}
 else {labelAt(8,42,"Settings",cyan,2);labelAt(8,75,"Factor: "+String(factor,4));labelAt(8,95,"Points: "+String(points));labelAt(8,115,factorSaved?"Factor saved in flash":"Factor not saved");btn(216,185,100,48,"TEST LOAD");btn(4,185,100,48,"BACK");btn(110,185,100,48,"RESET CAL");}}
 if((full||operationChanged)&&page==1)btn(4,185,100,48,operation?"CANCEL":page==0?"TARE / ZERO":"BACK");
 bool online=havePrevious&&millis()-last<3000;
 if(page==0){
 wifiIcon();
 String status="Estimate weight in CalFit";
 String weight="PHOTO";
 if(cached[1]!=weight){cached[1]=weight;int sz=weight.length()<=8?3:2;
 if(weightCanvas.getBuffer()){weightCanvas.fillScreen(panel);weightCanvas.setTextWrap(false);weightCanvas.setTextColor(cyan);weightCanvas.setTextSize(sz);weightCanvas.setCursor((155-6*sz*int(weight.length()))/2,(32-8*sz)/2);weightCanvas.print(weight);tft.drawRGBBitmap(82,82,weightCanvas.getBuffer(),155,32);}
 else {tft.fillRect(82,82,155,32,panel);labelAt(82,86,weight,cyan,sz);}}
 String info=status;
 if(cached[0]!=info){cached[0]=info;tft.fillRect(20,119,280,12,panel);labelAt(20+(280-6*int(info.length()))/2,121,info,DASH_MUTED);}
 String msg=notice.substring(0,50);
 if(cached[4]!=msg){cached[4]=msg;tft.fillRect(8,35,304,8,DASH_BG);labelAt(8,35,msg,DASH_MUTED);}
 }else if(page==1){field(1,8,65,3,17,String(grams)+" g");}
 if(page==1){field(4,8,157,1,50,notice);field(5,8,169,1,50,operation?String(count)+"/"+String(N)+" samples - tap left button to cancel":"");}
}
bool hit(int x,int y,int bx,int by,int w,int h){return x>=bx&&x<bx+w&&y>=by&&y<by+h;}
void handleTouch(){
 static unsigned long lastPoll=0;
 unsigned long now=millis();
 if(now-lastPoll<15)return;
 lastPoll=now;
 digitalWrite(TFT_CS,HIGH);
 if(!touch.touched()){if(motorDirection){motorStop();motorButtons();}pressed=false;return;}
 TS_Point p=touch.getPoint();
 int tx=p.x,ty=p.y;
 if(SWAP_XY){int tmp=tx;tx=ty;ty=tmp;}
 int x=constrain(map(tx,TOUCH_MIN_X,TOUCH_MAX_X,0,tft.width()-1),0L,long(tft.width()-1));
 int y=constrain(map(ty,TOUCH_MIN_Y,TOUCH_MAX_Y,0,tft.height()-1),0L,long(tft.height()-1));
 if(INVERT_X)x=tft.width()-1-x;
 if(INVERT_Y)y=tft.height()-1-y;
 if(y>=120)lastTouch=now;
 if(sleeping){if(y>=120){sleeping=false;pressed=true;redraw=true;lastTouch=now;}return;}
 if(motorDirection && (page!=4 || operation || cameraAction(x,y)!=motorDirection)){motorStop();motorButtons();}
 if(pressed)return;
 pressed=true;
 Serial.printf("Touch: Raw X:%d | Raw Y:%d | Z:%d | X:%d | Y:%d\n",p.x,p.y,p.z,x,y);
 if(operation){if(hit(x,y,4,185,100,48)){execute("x");redraw=true;}return;}
 if(page==1){
   const int bx[]={8,86,164,242};const int d[]={-100,-10,10,100};
   for(int i=0;i<4;i++)if(hit(x,y,bx[i],123,70,30)){grams=constrain(grams+d[i],10,5000);return;}
 }
 int b=-1;
 if(hit(x,y,4,185,100,48))b=0;
 else if(hit(x,y,110,185,100,48))b=1;
 else if(hit(x,y,216,185,100,48))b=2;
 if(page==4){
   Serial.printf("[TOUCH CAM] x=%d y=%d action=%d\n",x,y,cameraAction(x,y));
   if(hit(x,y,6,218,100,19)){motorStop();photoFrozen=false;page=0;redraw=true;}
   else if(!captureRequested && !pendingScan && cameraAction(x,y)==1){photoFrozen=false;motorStart(1);motorButtons();}
   else if(!captureRequested && !pendingScan && cameraAction(x,y)==-1){photoFrozen=false;motorStart(-1);motorButtons();}
   else if(cameraAction(x,y)==2){requestCapture();}
   return;
 }
 if(page==0 && hit(x,y,4,143,312,36)){page=4;redraw=true;return;}
 if(b<0)return;
 if(page==0){return;}
 else if(page==1){if(b==0)page=0;else if(!tared)notice="Tare first";else execute(String(b==1?"c ":"a ")+grams);}
 else if(page==2){if(b==0)page=0;else if(b==1){execute("r");page=0;}else {page=0;execute("d");}}
 else if(page==3){if(b==0)page=0;else if(b==1){execute("t");page=0;}}
 redraw=true;
}

// UART request/response: F -> CFBT + little-endian length + JPEG.
HardwareSerial camera(2);
constexpr uint32_t CAM_MAX=65000;
uint8_t *jpeg=nullptr;
uint32_t camLen=0, camPos=0, camSince=0, camNext=0;
uint32_t fpsStart=0, fpsFrames=0; float previewFPS=0;
bool captureFrame=false;
uint8_t camState=0, magicPos=0, lenPos=0;
String camStatus="Waiting for camera";
const char magic[]="CFBT";
bool cameraBlock(int16_t x,int16_t y,uint16_t w,uint16_t h,uint16_t *pixels){
 if(sleeping || page!=4 || x<0 || y<0 || x+w>160 || y+h>120)return false;
 tft.drawRGBBitmap(x+80,y+25,pixels,w,h);return true;
}
void cameraBegin(){
 jpeg=(uint8_t*)heap_caps_malloc(CAM_MAX,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(!jpeg)jpeg=(uint8_t*)malloc(CAM_MAX);
 camera.setRxBufferSize(32768);camera.begin(921600,SERIAL_8N1,16,17);
 TJpgDec.setJpgScale(2);TJpgDec.setSwapBytes(false);TJpgDec.setCallback(cameraBlock);
 Serial.println(jpeg?"[CAM] UART ready":"[CAM] Buffer allocation failed");
}
void motorButtons(){
 if(page!=4)return;
 for(int i=0;i<3;i++){
  int x=CTRL_X+i*104;bool active=i==0?motorDirection>0:i==2?motorDirection<0:(captureRequested||pendingScan);
  tft.fillRoundRect(x,CTRL_Y,CTRL_W,CTRL_H,6,active?cyan:panel);
  const char *name=i==0?"UP":i==1?"CAPTURE":"DOWN";
  labelAt(x+(CTRL_W-12*strlen(name))/2,CTRL_Y+12,name,ST77XX_WHITE,2);
 }
}
void cameraUI(bool full){
 if(full){tft.fillScreen(DASH_BG);labelAt(8,5,"CalFitBot",cyan,2);labelAt(236,9,"LIVE CAM");tft.drawRect(79,24,162,122,DASH_MUTED);btn(6,218,100,19,"BACK");motorButtons();for(int i=0;i<6;i++)cached[i]="";}
 bool valid=havePrevious&&millis()-last<3000&&filterReady&&!saturated&&!operation;
 String status=pendingScan?"Waiting for CalFit ACK":captureRequested?"Capturing fresh photo":camStatus;
 String weight="Photo-only mode";
 if(cached[0]!=status){cached[0]=status;tft.fillRect(110,218,210,19,DASH_BG);labelAt(112,224,status.substring(0,34),cyan);}
 if(cached[1]!=weight){cached[1]=weight;tft.fillRect(0,151,320,17,DASH_BG);String line=weight;labelAt((320-12*int(line.length()))/2,151,line,ST77XX_WHITE,2);}
}
void cameraTick(){
 if(!jpeg)return;
 uint32_t now=millis();
 if(captureRequested && now-captureAt>5000){captureRequested=false;camStatus="Capture timeout";motorButtons();}
 if(motorDirection)return;
 if(camState && now-camSince>2500){camState=0;camNext=now+500;camStatus="Camera timeout";Serial.println("[CAM] Timeout");}
 if(!camState){
  while(camera.available())camera.read();
  if((page!=4||sleeping||operation||pendingScan)&&!captureRequested){fpsStart=0;fpsFrames=0;previewFPS=0;return;} if(int32_t(now-camNext)<0)return;
  captureFrame=captureRequested;camera.write('F');camState=1;magicPos=lenPos=0;camLen=camPos=0;camSince=now;
 }
 for(int budget=0;budget<8192&&camera.available()&&camState;budget++){
  uint8_t b=camera.read();
  if(camState==1){if(b==magic[magicPos]){if(++magicPos==4)camState=2;}else magicPos=(b=='C');}
  else if(camState==2){camLen|=uint32_t(b)<<(8*lenPos++);if(lenPos==4){if(camLen<4||camLen>CAM_MAX){camState=0;camNext=now+500;camStatus="Invalid frame length";}else camState=3;}}
  else if(camState==3){jpeg[camPos++]=b;if(camPos==camLen){
   camState=0;camNext=millis()+5;
   if(jpeg[0]!=0xff||jpeg[1]!=0xd8||jpeg[camLen-2]!=0xff||jpeg[camLen-1]!=0xd9){camStatus="Invalid JPEG";return;}
   if(captureRequested && captureFrame){
    captureRequested=false;
    if(scanPhoto){memcpy(scanPhoto,jpeg,camLen);scanBytes=camLen;scanWeight=0;scanId=String(esp_random(),HEX)+"-"+String(esp_random(),HEX);pendingScan=true;acked=false;camStatus="Waiting for CalFit ACK";}
    else camStatus="Photo memory unavailable";
    if(page==4&&!sleeping)motorButtons();
   }
   if(page==4&&!sleeping&&!operation){auto result=TJpgDec.drawJpg(0,0,jpeg,camLen);if(result==0){uint32_t tick=millis(); if(!fpsStart)fpsStart=tick; else {++fpsFrames; if(tick-fpsStart>=1000){previewFPS=1000.0f*fpsFrames/(tick-fpsStart);fpsStart=tick;fpsFrames=0;}} camStatus=String(previewFPS,1)+" FPS | "+camLen+" bytes";}else camStatus="JPEG decode error";}
  }}
 }
}


bool weightValid(){return havePrevious && millis()-last<3000 && tared && factor!=0 && filterReady && !saturated && !operation && isfinite(filtered);}
void requestCapture(){
 if(pendingScan||captureRequested)return;
 if(!scanPhoto||!jpeg){camStatus="Photo memory unavailable";return;}
 // Discard any in-flight preview before requesting a new frame.
 captureRequested=true;captureAt=millis();
 if(camState) { /* finish and discard current preview first */ }
 motorButtons();
}
void networkBegin(){
 scanPhoto=(uint8_t*)heap_caps_malloc(CAM_MAX,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);
 if(!scanPhoto)scanPhoto=(uint8_t*)malloc(CAM_MAX);
 WiFi.mode(WIFI_STA);WiFi.setAutoReconnect(true);WiFi.begin("calfit","YOUR_HOTSPOT_PASSWORD");
 server.on("/status",HTTP_GET,[]{server.send(200,"application/json",String("{\"device\":\"CalFitBot\",\"protocol\":1,\"pending\":")+(pendingScan?"true":"false")+"}");});
 server.on("/weight",HTTP_GET,[]{server.send(200,"application/json",String("{\"weightGrams\":")+String("null")+"}");});
 server.on("/scan",HTTP_GET,[]{if(!pendingScan){server.send(204);return;}server.send(200,"application/json",String("{\"scanId\":\"")+scanId+"\",\"weightGrams\":"+String("null")+",\"photoBytes\":"+String(scanBytes)+"}");});
 server.on("/photo",HTTP_GET,[]{if(!pendingScan||server.arg("id")!=scanId){server.send(404);return;}server.send_P(200,"image/jpeg",(const char*)scanPhoto,scanBytes);});
 server.on("/ack",HTTP_POST,[]{if(server.arg("id")!=scanId||(!pendingScan&&!acked)){server.send(404);return;}pendingScan=false;acked=true;camStatus="Sent to CalFit";if(page==4&&!sleeping)redraw=true;server.send(200,"application/json","{\"ok\":true}");});
 server.begin();
}
void networkTick(){
 static bool mdns=false;static uint32_t retry=0;
 if(WiFi.status()==WL_CONNECTED){if(!mdns&&millis()-retry>=2000){retry=millis();mdns=MDNS.begin("calfitbot");if(mdns)MDNS.addService("calfitbot","tcp",80);}server.handleClient();}
 else if(mdns){MDNS.end();mdns=false;}
}
void wifiIcon(){
 uint16_t c=WiFi.status()==WL_CONNECTED?ST77XX_GREEN:DASH_MUTED;
 tft.fillRect(280,5,36,29,DASH_BG);
 for(int r=7;r<=19;r+=6){for(int a=225;a<=315;a+=5){float rad=a*PI/180.0f;tft.drawPixel(298+roundf(r*cosf(rad)),28+roundf(r*sinf(rad)),c);}}
 tft.fillCircle(298,28,2,c);
}
void standbyTick(){
 uint32_t now=millis();
 if(!sleeping && !pressed && !operation && !captureRequested && now-lastTouch>=30000){motorStop();sleeping=true;tft.fillScreen(ST77XX_BLACK);sleepFrame=0;}
 if(!sleeping||now-sleepFrame<250)return;
 sleepFrame=now;
 tft.fillRect(65,45,250,135,ST77XX_BLACK);
 int breath=(now/250)%8; if(breath>4)breath=8-breath;
 for(int i=0;i<2;i++){int cx=110+i*100;for(int dx=-25;dx<=25;dx++){int yy=105+int(13*(1.0f-dx*dx/625.0f));tft.fillCircle(cx+dx,yy,2,cyan);}}
 tft.drawCircle(160,145,5+breath,cyan);
 for(int i=0;i<3;i++)labelAt(230+i*23,80-i*19-((now/250)%4)*3,"z",cyan,1+i);
 labelAt(103,190,"Sleeping...",DASH_MUTED,2);labelAt(67,222,"Touch lower screen to wake",DASH_MUTED);
}
