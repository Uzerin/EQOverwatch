namespace EQOverwatch.Core.Maps;

/// <summary>
/// The short name EverQuest uses for each zone's files (<c>poknowledge</c> for The Plane of Knowledge), by zone number. EQ's own
/// <c>Resources\ZoneNames.txt</c> gives each number's full name and expansion, so the two together match a "You have entered ..."
/// line, or a map file, to its zone. Zones missing here still show in the Maps tab under their short name.
/// </summary>
public static class ZoneShortNames
{
    /// <summary>Zone number to short name.</summary>
    public static IReadOnlyDictionary<int, string> ById { get; } = Build();

    private static Dictionary<int, string> Build()
    {
        const string Table =
            // Classic
            "1 qeynos;2 qeynos2;3 qrg;4 qeytoqrg;6 highkeep;8 freportn;9 freportw;10 freporte;11 runnyeye;12 qey2hh1;13 northkarana;"
            + "14 southkarana;15 eastkarana;16 beholder;17 blackburrow;18 paw;19 rivervale;20 kithicor;21 commons;22 ecommons;23 erudnint;"
            + "24 erudnext;25 nektulos;26 cshome;27 lavastorm;29 halas;30 everfrost;31 soldunga;32 soldungb;34 nro;35 sro;36 befallen;"
            + "37 oasis;39 hole;40 neriaka;41 neriakb;42 neriakc;43 neriakd;44 najena;45 qcat;47 feerrott;48 cazicthule;49 oggok;"
            + "50 rathemtn;51 lakerathe;52 grobb;53 aviak;54 gfaydark;55 akanon;57 lfaydark;58 crushbone;59 mistmoore;60 kaladima;"
            + "61 felwithea;62 felwitheb;63 unrest;64 kedge;65 guktop;66 gukbottom;67 kaladimb;68 butcher;69 oot;70 cauldron;71 airplane;"
            + "72 fearplane;73 permafrost;75 paineel;76 hateplane;77 arena;80 soltemple;98 erudsxing;100 stonebrunt;101 warrens;"
            + "151 bazaar;181 jaggedpine;186 hateplaneb;187 shadowrest;"
            // Kunark
            + "78 fieldofbone;79 warslikswood;81 droga;82 cabwest;83 swampofnohope;84 firiona;85 lakeofillomen;86 dreadlands;"
            + "87 burningwood;88 kaesora;89 sebilis;90 citymist;91 skyfire;92 frontiermtns;93 overthere;94 emeraldjungle;95 trakanon;"
            + "96 timorous;97 kurn;102 karnor;103 chardok;104 dalnir;105 charasis;106 cabeast;107 nurga;108 veeshan;109 veksar;277 chardokb;"
            // Velious
            + "110 iceclad;111 frozenshadow;112 velketor;113 kael;114 skyshrine;115 thurgadina;116 eastwastes;117 cobaltscar;"
            + "118 greatdivide;119 wakening;120 westwastes;121 crystal;123 necropolis;124 templeveeshan;125 sirens;126 mischiefplane;"
            + "127 growthplane;128 sleeper;129 thurgadinb;"
            // Luclin
            + "150 shadowhaven;152 nexus;153 echo;154 acrylia;155 sharvahl;156 paludal;157 fungusgrove;158 vexthal;159 sseru;160 katta;"
            + "161 netherbian;162 ssratemple;163 griegsend;164 thedeep;165 shadeweaver;166 hollowshade;167 grimling;168 mseru;"
            + "169 letalis;170 twilight;171 thegrey;172 tenebrous;173 maiden;174 dawnshroud;175 scarlet;176 umbral;179 akheva;"
            // Planes of Power
            + "200 codecay;201 pojustice;202 poknowledge;203 potranquility;204 ponightmare;205 podisease;206 poinnovation;207 potorment;"
            + "208 povalor;209 bothunder;210 postorms;211 hohonora;212 solrotower;213 powar;214 potactics;215 poair;216 powater;217 pofire;"
            + "218 poeartha;219 potimea;220 hohonorb;221 nightmareb;222 poearthb;223 potimeb;"
            // Legacy of Ykesha
            + "224 gunthak;225 dulak;226 torgiran;227 nadox;228 hatesfury;278 soldungc;"
            // Lost Dungeons of Norrath
            + "229 guka;230 ruja;231 taka;232 mira;233 mmca;234 gukb;235 rujb;236 takb;237 mirb;238 mmcb;239 gukc;240 rujc;241 takc;"
            + "242 mirc;243 mmcc;244 gukd;245 rujd;246 takd;247 mird;248 mmcd;249 guke;250 ruje;251 take;252 mire;253 mmce;254 gukf;"
            + "255 rujf;256 takf;257 mirf;258 mmcf;259 gukg;260 rujg;261 takg;262 mirg;263 mmcg;264 gukh;265 rujh;266 takh;267 mirh;"
            + "268 mmch;269 ruji;270 taki;271 miri;272 mmci;273 rujj;274 takj;275 mirj;276 mmcj;"
            // Gates of Discord
            + "182 nedaria;279 abysmal;280 natimbi;281 qinimi;282 riwwi;283 barindu;284 ferubi;285 snpool;286 snlair;287 snplant;"
            + "288 sncrematory;289 tipt;290 vxed;291 yxtta;292 uqua;293 kodtaz;294 ikkinz;295 qvic;296 inktuta;297 txevu;298 tacvi;"
            // Omens of War
            + "300 wallofslaughter;301 bloodfields;302 draniksscar;303 causeway;304 chambersa;305 chambersb;306 chambersc;307 chambersd;"
            + "308 chamberse;309 chambersf;316 provinggrounds;317 anguish;318 dranikhollowsa;319 dranikhollowsb;320 dranikhollowsc;"
            + "321 dranikhollowsd;322 dranikhollowse;323 dranikhollowsf;324 dranikhollowsg;325 dranikhollowsh;326 dranikhollowsi;"
            + "327 dranikhollowsj;328 dranikcatacombsa;329 dranikcatacombsb;330 dranikcatacombsc;331 draniksewersa;332 draniksewersb;"
            + "333 draniksewersc;334 riftseekers;335 harbingers;336 dranik;"
            // Dragons of Norrath
            + "337 broodlands;338 stillmoona;339 stillmoonb;340 thundercrest;341 delvea;342 delveb;343 thenest;344 guildlobby;345 guildhall;"
            // Depths of Darkhollow
            + "347 illsalin;348 illsalina;349 illsalinb;350 illsalinc;351 dreadspire;354 drachnidhive;355 drachnidhivea;356 drachnidhiveb;"
            + "357 drachnidhivec;358 westkorlach;359 westkorlacha;360 westkorlachb;361 westkorlachc;362 eastkorlach;363 eastkorlacha;"
            + "364 shadowspine;365 corathus;366 corathusa;367 corathusb;368 nektulosa;"
            // Prophecy of Ro
            + "369 arcstone;370 relic;371 skylance;372 devastation;373 devastationa;374 rage;375 ragea;376 takishruins;377 takishruinsa;"
            + "378 elddar;379 elddara;380 theater;381 theatera;"
            // Revamped Freeport, Ro and other Classic zones
            + "382 freeporteast;383 freeportwest;384 freeportsewers;385 freeportacademy;386 freeporttemple;387 freeportmilitia;"
            + "388 freeportarena;389 freeportcityhall;390 freeporttheater;391 freeporthall;392 northro;393 southro;407 highpasshold;"
            + "408 commonlands;409 oceanoftears;413 innothuleb;414 toxxulia;415 mistythicket;448 steamfontmts;"
            // The Serpent's Spine
            + "394 crescent;395 moors;396 stonehive;397 mesa;398 roost;399 steppes;400 icefall;401 valdeholm;402 frostcrypt;403 sunderock;"
            + "404 vergalid;405 direwind;406 ashengate;"
            // The Buried Sea
            + "416 kattacastrum;417 thalassius;418 atiiki;419 zhisza;420 silyssar;421 solteris;422 barren;423 buriedsea;424 jardelshook;"
            + "425 monkeyrock;426 suncrest;427 deadbone;428 blacksail;429 maidensgrave;430 redfeather;431 shipmvp;432 shipmvu;"
            + "433 shippvu;434 shipuvu;435 shipmvm;"
            // Secrets of Faydwer
            + "436 mechanotus;437 mansion;438 steamfactory;439 shipworkshop;440 gyrospireb;441 gyrospirez;442 dragonscale;443 lopingplains;"
            + "444 hillsofshade;445 bloodmoon;446 crystallos;447 guardian;449 cryptofshade;450 dragonscalea;451 dragonscaleb;"
            // Seeds of Destruction
            + "452 oldfieldofbone;453 oldkaesoraa;454 oldkaesorab;455 oldkurn;456 oldkithicor;457 oldcommons;459 thevoida;460 thevoidb;"
            + "461 thevoidc;462 thevoidd;463 thevoide;464 thevoidf;465 thevoidg;466 oceangreenhills;467 oceangreenvillage;"
            + "468 oldblackburrow;469 bertoxtemple;470 discord;471 discordtower;472 oldbloodfield;473 precipiceofwar;474 olddranik;"
            + "475 toskirakk;476 korascian;477 rathechamber;478 oldfieldofboneb;"
            // Underfoot
            + "480 brellsrest;481 fungalforest;482 underquarry;483 coolingchamber;484 shiningcity;485 arthicrex;486 foundation;"
            + "487 lichencreep;488 pellucid;489 stonesnake;490 brellstemple;491 convorteum;492 brellsarena;493 weddingchapel;"
            + "494 weddingchapeldark;495 dragoncrypt;"
            // House of Thule
            + "700 feerrott2;701 thulehouse1;702 thulehouse2;703 housegarden;704 thulelibrary;705 well;706 fallen;707 morellcastle;"
            + "708 somnium;709 alkabormare;710 miragulmare;711 thuledream;712 neighborhood;713 phylactery;736 shadowedmount;"
            // Veil of Alaris
            + "724 argath;725 arelis;726 sarithcity;727 rubak;728 beastdomain;729 resplendent;730 pillarsalra;731 windsong;"
            + "732 cityofbronze;733 sepulcher;734 eastsepulcher;735 westsepulcher;"
            // Rain of Fear
            + "752 shardslanding;753 xorbb;754 kaelshard;755 eastwastesshard;756 crystalshard;757 breedinggrounds;758 eviltree;"
            + "759 grelleth;760 chapterhouse;763 chelsithreborn;764 poshadow;765 heartoffear;768 heartoffearb;769 heartoffearc;"
            // Call of the Forsaken
            + "770 bixiewarfront;771 deadhills;772 ethernere;773 thevoidh;775 towerofrot;776 arginhiz;"
            // The Darkened Sea
            + "778 arxmentis;779 brotherisland;780 kattacastrumb;781 dredge;782 endlesscaverns;783 thuliasaur;784 degmar;785 tempesttemple;"
            // The Broken Mirror
            + "777 exalted;795 cosul;796 codecayb;797 exaltedb;798 pohealth;"
            // Empires of Kunark
            + "788 drogab;790 scorchedwoods;791 frontiermtnsb;793 korshaext;794 lceanium;799 korshaint;800 chardoktwo;"
            // Ring of Scale
            + "792 gorowyn;813 charasistwo;814 skyfiretwo;815 overtheretwo;816 veeshantwo;"
            // The Burning Lands
            + "787 gnomemtn;817 trialsofsmoke;818 stratos;819 aalishai;820 empyr;821 esianti;822 mearatas;823 chamberoftears;"
            // Torment of Velious
            + "824 eastwastestwo;825 frozenshadowtwo;826 crystaltwoa;827 greatdividetwo;828 velketortwo;829 kaeltwo;830 crystaltwob;"
            // Claws of Veeshan
            + "831 sleepertwo;832 necropolistwo;833 cobaltscartwo;834 westwastestwo;835 skyshrinetwo;836 templeveeshantwo;"
            // Terror of Luclin
            + "843 maidentwo;844 umbraltwo;845 akhevatwo;846 vexthaltwo;847 shadowvalley;848 basilica;849 bloodfalls;"
            // Night of Shadows
            + "851 shadowhaventwo;852 sharvahltwo;853 paludaltwo;854 shadeweavertwo;855 darklightcaverns;856 deepshade;857 firefallpass;"
            + "858 hollowshadetwo;"
            // Laurion's Song
            + "859 laurioninn;860 ankexfen;861 pallomen;862 herosforge;863 moorsofnokk;864 unkemptwoods;865 timorousfalls;"
            // The Outer Brood
            + "870 hodstock;871 toe;872 aureatecovert;873 harbingerscradle;874 puissance;875 gildedspire;"
            // Newest
            + "879 candlemakers;882 ruinedrelic;883 vortex;884 spite;886 giftygiver;998 fhalls";

        var map = new Dictionary<int, string>();
        foreach (string pair in Table.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = pair.IndexOf(' ');
            map[int.Parse(pair.AsSpan(0, space), System.Globalization.CultureInfo.InvariantCulture)] = pair[(space + 1)..];
        }

        return map;
    }
}
